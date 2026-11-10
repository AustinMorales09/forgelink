# Operations, deployment and change control

## Deployment topology

| Component | Where | Notes |
|---|---|---|
| ForgeLink.Web | Azure App Service / Container Apps (or an on-prem VM per plant) | Stateless. Scale out ≥ 2 instances for availability. Health: `/health/live`, `/health/ready` (DB check) |
| Database | Azure SQL / SQL Server (Always On for on-prem) | Schema changes ship as an EF Core migration bundle, applied by the pipeline, never by the app at runtime |
| MQTT | Mosquitto / HiveMQ on the plant network, or Azure Event Grid MQTT | TLS + client certificates per gateway in production |
| WPF station | Industrial PCs, deployed via Intune/MSIX or ClickOnce | Runs as a standard user; config from `appsettings.json`, API key from the Windows credential store |
| MAUI handheld | Android Enterprise / Zebra devices via MDM | Base URL and device key pushed through managed app config into `Preferences`/`SecureStorage` |

**Multiple instances.** Each delivery carries an idempotency key, so two dispatchers sending the same message cannot double-book in the ERP.
To avoid wasted calls, run the dispatcher on one instance (`Workers:Enabled=false` on the others) or add a lease row in `SyncStates`.

**Plant isolation.** Shop-floor writes depend only on the local database. An ERP or WAN outage makes the outbox grow; it does not stop production.

## Configuration and secrets

* `Authentication:Mode=EntraId` in every non-dev environment. The app refuses to start with the Development scheme outside Development.
* Entra app registration: expose an API scope and define app roles `Operator`, `Supervisor`, `Integration`, `Admin`.
  Assign them to security groups, not to individuals.
* Device API keys: store only `KeyHash` (SHA-256 hex) in configuration and rotate them per device.
* ERP API key and webhook secret come from Key Vault (`AddAzureKeyVault`) or the user-secrets store in development.

## Change control and validation (production uptime matters)

1. **Every change starts in a PR.** CI must pass: build with warnings as errors, unit and integration tests, and the vulnerable-package gate.
2. **Database changes** are additive first (expand → migrate → contract) so old and new app versions can run side by side during a rolling deploy.
   Stored procedures are `CREATE OR ALTER` and idempotent.
3. **Release to a staging plant line** that is connected to the ERP test company and fed by the OT simulator. Run the demo script as the operational qualification checklist.
4. **Production deploys happen in planned windows** (shift change or planned maintenance) with a rollback plan: a slot swap for the web app and a pinned previous MSIX for stations.
   Stations keep working offline throughout, and their queues drain when the server is back.
5. **Traceability for auditors**: the audit trail covers who, what, when, and the before/after values. The inventory ledger is immutable. Outbox rows show exactly what was sent to the ERP and when, with the ERP's reference.

## Runbooks

### Dead-lettered ERP message
1. Open `/integration?status=DeadLettered` and read `LastError` (for example, "Item BRK-2001 does not exist in the ERP item master").
2. Fix the root cause in the ERP (create the item or open the period).
3. Press **Retry**. The partition unblocks and later messages for that SKU or order follow in order.
4. If the transaction must never post (for example, it was entered in error), **Cancel** it with a reason. This is audited, and the inventory needs a compensating adjustment.

### Outbox backlog growing
* Check `/integration` for the oldest pending age and the ERP simulator or ERP status.
* The circuit breaker opens after repeated failures, which protects the ERP while it recovers. Messages back off for up to 5 minutes.
* The backlog drains automatically when the ERP returns, so no action is needed unless messages are being dead-lettered.

### Machine shows Offline
* Heartbeats stopped for more than 30 s. Check the gateway and PLC power and network, and the broker connection in the ForgeLink logs (`MQTT connected…`).
* Downtime is booked as `NO_HEARTBEAT` until the first message after reconnect closes it.

### Station stuck "OFFLINE"
* It probes on every request and every 10 s. Press **Sync (F5)**.
* Items in the **Rejected** count were refused by the server on replay (for example, the lot was put on hold while the station was offline).
  They are kept on the device for a supervisor to review and are never retried automatically.

### Housekeeping
* `EXEC dbo.usp_PurgeIntegrationHistory @RetainDays = 30` purges delivered outbox rows and old idempotency records in batches. Schedule it nightly.
* `EXEC dbo.usp_UnacknowledgedTransactions @GraceMinutes = 30` lists inventory transactions with no ERP acknowledgement. Use it for the nightly reconciliation.
