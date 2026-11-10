# ADR 0002: Store-and-forward on stations and handhelds

**Status:** accepted

## Context
Industrial Wi-Fi has dead spots (steel racking, cranes), and switches reboot during maintenance. If an operator cannot report production or a forklift driver cannot confirm a move, people go back to paper, and paper becomes data-entry errors later.

## Decision
* `StoreAndForwardForgeLinkApi` wraps the HTTP client. **Commands** (report production, move, count, inspect) are attempted online first.
  On a transient failure they are written to a local SQLite queue (WAL, `synchronous=FULL`), and the UI receives a `QueuedForSyncException`, which it shows as "saved, will sync" rather than as an error.
* While older commands are queued, new commands queue behind them to preserve order.
* Replay uses each command's **original idempotency key**, so a command whose response was lost (the server committed but the client timed out) is not applied twice.
* Commands the server rejects on replay (4xx) move to **Rejected**. They stay visible on the device and are never retried automatically.
* **Queries and decisions are not queued.** Looking up stock, starting a job or approving a count needs the server's current answer.
* Lot numbers are deterministic (`{WO}-{yyMMdd}`), so a label printed offline matches what the server will record.

## Consequences
* A station can run a whole shift disconnected. Inventory seen by other users lags until the station syncs.
* Business rules are still enforced by the server on replay, so a lot put on hold during the outage is caught then.
