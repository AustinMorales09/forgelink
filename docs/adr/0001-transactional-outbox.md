# ADR 0001: Transactional outbox for all ERP postings

**Status:** accepted

## Context
Every production report, inventory move, adjustment, labor record and quality hold must reach the ERP for costing, WIP and inventory valuation.
The ERP has maintenance windows and slow periods (month-end close), and it is sometimes reachable only over a WAN.
If the ERP is called inline during the operator's request:
* the shop floor stops whenever the ERP is down, and
* a crash between the local commit and the ERP call loses or duplicates financial transactions.

## Decision
* Application services write the business change **and** an `OutboxMessage` in the same `SaveChanges`, so they commit or roll back together.
* A background dispatcher delivers messages with an `Idempotency-Key`. The ERP must deduplicate on that key, and a `409` counts as delivered.
* Outcomes are classified: transient failures (timeout, 5xx, 429, open circuit) are retried with exponential backoff and jitter. Permanent failures (4xx) are dead-lettered.
* Each message has a partition key (`inv:{sku}`, `wo:{number}`, `lot:{lot}`, `labor:{badge}`). Order is strict within a partition, and a stuck partition does not block the others.
* The state is saved after **each** message, so a crash re-sends at most one message, and the ERP deduplicates it.

## Consequences
* The ERP sees changes after a short delay (seconds in normal operation). The dashboard shows backlog and age, so the delay stays visible.
* Dead letters need a person. The MVC console provides retry and cancel actions, with audit.
* Outbox rows double as an integration audit log; `usp_PurgeIntegrationHistory` removes old ones.
