# ForgeLink v0.1: status, data storage, and integration roadmap

_As of 2026-11-17 (v0.2: API docs + rate limits + pagination, push updates, daily OEE pipeline, stack smoke test in CI)._

## 1. What works today

Legend: ✅ verified by running it · 🧪 covered by automated tests · 📝 written and compiles, not exercised.

### Server: `ForgeLink.Web` (ASP.NET Core, .NET 10)
| Capability | Status |
|---|---|
| REST API: work orders (list / start / pause / complete / report production), inventory (query, move, adjust, cycle count, approve), quality checks, lot trace, machines, KPIs, integration health | ✅ 🧪 |
| `Idempotency-Key` on all commands: replay returns the stored response; reusing a key with a different body → 422 | ✅ 🧪 |
| Problem-details errors with stable `code` (`inventory.insufficient`, `lot.on-hold`, …) | ✅ 🧪 |
| Blazor Server plant dashboard: OEE (A/P/Q), downtime Pareto, hourly throughput, machine board, work orders, inventory, move, cycle count, quality, lot trace | ✅ |
| MVC integration console: outbox by status, message detail and payload, Retry / Cancel (audited), "Sync master data now", audit trail | ✅ 🧪 |
| Transactional outbox → ERP: per-partition FIFO, exponential backoff + jitter, dead-lettering on 4xx, give-up after N attempts | ✅ 🧪 (6/6 delivered live with a 15 % simulated failure rate) |
| ERP master-data sync: items and work orders on a watermark; run-time conflicts reported, not applied | ✅ 🧪 |
| ERP webhook with HMAC-SHA256 + timestamp replay window | ✅ 🧪 |
| MQTT telemetry ingestion (embedded broker locally): state, counts, heartbeats → machine state, downtime events, hourly counters | ✅ 🧪 |
| Heartbeat watchdog → machine Offline with `NO_HEARTBEAT` downtime | 🧪 |
| HTTP telemetry endpoint for non-MQTT gateways | 🧪 |
| Auth: development scheme, device API keys + roles, fallback "must be authenticated" policy | ✅ 🧪 |
| Auth: Microsoft Entra ID (JWT for API, OIDC for UI) | 📝 needs a real tenant / app registration |
| Audit trail (before/after JSON), optimistic concurrency on balances and work orders | 🧪 |
| SQL Server provider + stored procedures (Pareto, purge, reconciliation) | 📝 SQLite was used for all runs; SQL Server path not executed |
| SignalR hub (`/hubs/shopfloor`) + in-process events: Blazor views refresh ~0.4 s after a change (coalesced), 30 s safety poll | ✅ 🧪 (a real SignalR client receives hub events in the integration tests; machine card updated within 1 s in the browser) |
| OpenAPI 3.1 document (`/openapi/v1.json`) with security schemes and XML-doc summaries; Scalar reference at `/docs` | ✅ 🧪 |
| Per-caller token-bucket rate limiting on `/api` (separate telemetry budget), 429 problem details + `Retry-After` | 🧪 |
| Keyset (cursor) pagination: `GET /api/inventory/ledger?limit=&cursor=` | ✅ 🧪 |
| Daily OEE pipeline: `KpiRollupWorker` → `DailyMachineKpis` (plant time zone, DST, late-data window, idempotent upsert); `GET /api/reports/oee/daily` (JSON/CSV), `POST …/rollup`; `/reports/oee` page with heat map | ✅ 🧪 |

### Clients
| Capability | Status |
|---|---|
| Shared Razor UI library used by both Blazor Server and MAUI | ✅ |
| **MAUI Blazor Hybrid (Windows)**: home, lookup with live server data, offline indicator | ✅ |
| **MAUI (Android)**: signed debug APK builds | 📝 not run on a device or emulator |
| MAUI live updates via `HubShopFloorEvents` (SignalR client, capped-backoff reconnect) | 🧪 client class tested against the server; 📝 not run inside the MAUI app |
| Offline store-and-forward client (SQLite queue, ordered replay with original keys, rejected-item quarantine) | 🧪 (integration test drains a queue into the live server) |
| **WPF operator station**: badge / WO scanning, start / pause / complete, weigh-and-count, report, ZPL label, offline queue, F5/F9 keys | ✅ window opens, connects and shows live machine state · 📝 scan-to-report workflow not exercised by hand |
| Device adapters: GS1-128 / DataMatrix scan parser, ZPL label builder (injection-safe), scale protocol parser, serial scale, TCP 9100 printer | 🧪 parsers / builders · 📝 real serial and TCP hardware |

### Tooling
| Item | Status |
|---|---|
| ERP simulator (REST, API key, chaos 503s + latency, 422 validation, idempotency, signed webhooks) | ✅ |
| OT simulator (5 machines over MQTT, breakdowns with reason codes) | ✅ |
| 71 unit + 21 integration tests | ✅ all passing |
| GitHub Actions CI (build with warnings-as-errors, tests, vulnerable-package gate, MAUI Android) | ✅ green since the first push |
| Dockerfiles + `docker-compose.yml` (SQL Server, Mosquitto) + `tools/smoke/stack-smoke.sh`, run by the CI `stack` job | 📝 first run happens on the next push (no Docker on the dev machine) |

### Known limitations in v0.1
* Schema is created with `EnsureCreated`; there are no EF migrations yet. Changing the model means deleting the dev database (v0.2 added `DailyMachineKpis`, so delete `forgelink.db` once after upgrading).
* The OEE rollup assumes planned time = `Reporting:PlannedHoursPerDay` for every machine; there is no shift calendar yet. Counters are hourly UTC buckets, so plants in half-hour-offset time zones get day boundaries rounded to the hour.
* Rate-limit buckets live in process memory; behind several instances use a distributed limiter (or the gateway's) instead.
* The outbox dispatcher has no leader lease, so run it on a single instance (idempotency keeps duplicates harmless, but they waste calls).
* The ERP simulator keeps everything in memory, so restarting it forgets bookings and resets work-order numbering.
* The MAUI app uses typed / keyboard-wedge scanning only; there is no camera scanning yet.
* No user-facing login page in Development mode (single fixed dev user, overridable per request with `X-Dev-User` / `X-Dev-Roles`).

## 2. Where data is stored

| Data | Location (dev defaults) | Notes |
|---|---|---|
| **ForgeLink system of record**: items, work orders, labor, lots, genealogy, balances, inventory ledger, cycle counts, quality checks, machines, downtime, counters, outbox, audit, idempotency records, sync watermarks | `src/ForgeLink.Web/forgelink.db` (SQLite, created on first run) | Set `Database:Provider=SqlServer` + `ConnectionStrings:ForgeLink` for SQL Server (docker-compose does this). Delete the file to reset the demo; it re-seeds 8 h of shift history. |
| Outbound ERP messages awaiting delivery | `OutboxMessages` table in the same database | This is how "ERP down" never loses data: postings commit with the business change. |
| ERP simulator data | **In memory** in `ForgeLink.ErpSimulator` | Status at `http://localhost:5080/`; transactions at `/api/v1/transactions` (needs `X-Api-Key: dev-erp-key`). |
| MQTT messages in flight | Embedded broker in the web process (memory), or Mosquitto in docker-compose | Persistent sessions + QoS 1 hold messages while ForgeLink restarts (broker memory only for the embedded broker). |
| WPF station offline queue | `%LOCALAPPDATA%\ForgeLink\station-{MachineCode}-offline.db` | SQLite, WAL, `synchronous=FULL`. |
| WPF printed labels (file printer mode) | `%LOCALAPPDATA%\ForgeLink\labels\*.zpl` | Switch `Station:LabelPrinter:Mode` to `Tcp` for a real Zebra printer. |
| MAUI offline queue | App data directory: Windows `%LOCALAPPDATA%\ForgeLink\ForgeLink.Mobile\Data\handheld-offline.db`; Android app-private storage | |
| MAUI settings (server URL, device key) | MAUI `Preferences` (`ForgeLinkBaseUrl`, `ForgeLinkApiKey`) | Defaults point at `localhost:5100` (Windows) and `10.0.2.2:5100` (Android emulator). |
| Configuration and dev secrets | `appsettings*.json` per project | Dev keys are demo values only. In production, use Key Vault / user secrets and store only `KeyHash` for device keys. |

## 3. Next steps for adding integrations

ForgeLink is built around ports, so each new integration is an adapter rather than a rewrite.

### 3.1 A real ERP (Epicor Kinetic, SAP S/4HANA, Dynamics 365 F&O, Infor CloudSuite, Plex…)
1. **Implement `IErpClient`** (`src/ForgeLink.Application/Abstractions/IErpClient.cs`) in `Infrastructure/Erp/<Vendor>ErpClient.cs`:
   * map each `ErpMessageTypes` value to the vendor transaction. For example, production receipt → Epicor `Erp.BO.LaborSvc` / SAP `API_PRODUCTION_ORDER_2` confirmation / D365 production journal;
   * map vendor item and production-order feeds to `ErpItem` / `ErpWorkOrder`;
   * keep the outcome classification contract: 2xx/duplicate = success, 5xx/429/timeout = transient, business validation = permanent.
2. **Authentication**: OAuth2 client credentials (Entra ID for D365; SAP BTP / XSUAA; Epicor API key + token) via a `DelegatingHandler` that caches tokens.
3. **Idempotency on the ERP side**: if the ERP has no idempotency header, store our key in a reference field (journal "external ref") and look it up before re-posting.
4. **Selection by config**: `Erp:Adapter = Simulator | Epicor | SapS4 | D365`, wired in `Infrastructure/DependencyInjection.cs`.
5. **Contract tests**: record real request/response pairs against the vendor sandbox and replay them in CI (WireMock.Net), plus a nightly live test against the sandbox.
6. **Reconciliation**: schedule `usp_UnacknowledgedTransactions` and add an on-hand compare (ForgeLink balances vs ERP) per SKU/location.

### 3.2 OT connectivity beyond MQTT JSON
| Source | How to add it |
|---|---|
| **OPC UA** (PLCs, CNC controllers) | New `OpcUaTelemetrySource : BackgroundService` using the OPC Foundation .NET Standard stack. It subscribes to monitored items (`Machine.State`, part counter) and maps them to `MachineSignalDto`, then calls `TelemetryService`, the same entry point MQTT uses. Node IDs go in config per machine. |
| **MQTT Sparkplug B** | Decode protobuf payloads (`plant/{group}/{edge}/{device}` birth/data/death) in a second `IMqttPayloadParser`. Use DDEATH as an instant Offline signal instead of waiting for the watchdog. |
| **Historians / SCADA** (PI, Ignition, Wonderware) | Pull via REST or SQL on a schedule into `ProductionCounters`, or have Ignition publish to the existing HTTP `/api/machines/signals` endpoint with an Integration API key. |
| **Edge buffering** | Run the OT listener on an edge box per line (same code), forwarding over HTTPS with the client offline queue, so plants survive WAN loss. |

### 3.3 Devices
* **Camera scanning in MAUI**: add `ZXing.Net.MAUI` behind an `IScanner` interface, feeding the same `ScanParser`.
* **Zebra DataWedge intents** (instead of keystroke mode): an Android `BroadcastReceiver` in `Platforms/Android` raises scans into the Blazor `ScanInput`.
* **Printers**: add Zebra Link-OS status checks (paper out, head open) before printing, and a print queue so labels also survive printer outages.
* **Scales**: add vendor profiles (Mettler Toledo SICS, Rice Lake) as alternative `ScaleProtocol` parsers selected by config.

### 3.4 Enterprise plumbing
* **Event streaming**: publish outbox events to Azure Service Bus / Event Grid as well as the ERP, so MES, WMS, or a data lake can subscribe without new point-to-point code. Add a second dispatcher target; the partition key becomes the session ID for ordered delivery.
* **Reporting**: expose the read model to Power BI through SQL views or Fabric mirroring of the SQL Server database.
* **Identity**: create the Entra app registration with app roles `Operator`, `Supervisor`, `Integration` and `Admin`, map plant security groups, and switch `Authentication:Mode=EntraId`.
* **Schema change control**: replace `EnsureCreated` with EF Core migrations and a migration bundle applied by the release pipeline.

### Suggested order
1. EF migrations + SQL Server run (unblocks real deployments).
2. Real ERP adapter against a sandbox (biggest business value).
3. OPC UA source for one cell.
4. Entra ID tenant wiring.
5. Camera / DataWedge scanning and printer status.
