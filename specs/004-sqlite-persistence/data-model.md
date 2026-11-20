# Data model

The [canonical schema contract](../001-multibrand-redesign/schema-contract.md) defines logical entities, fields, relationships and state transitions.

- **Brands, administrators, catalog and stock** — Preserve per-brand membership, permissions and listings, with intentional shared products and inventory. Retain UUID keys and check that the SQLite representation preserves catalog cursor ordering.
- **Orders, payments, wallets and fulfillment** — Preserve exact amounts, denominations, immutable postings and original order relationships. Update balance, settlement and stock with the same atomic boundaries as today. Preserve encrypted values as blobs and keep keys separate.
- **Jobs and replay** — Persist deduplication keys, lease owner and generation, availability, attempts, and delivery outcomes. A stale owner cannot commit. Uncertain remote outcomes remain available for administrator review.
