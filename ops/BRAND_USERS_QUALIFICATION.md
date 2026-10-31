# T018–T020 brand membership and private-DM flow

Qualification pending. Reuse the canonical T008 brands, brand_channels and users tables; no 0003 migration or telegram_sessions table is necessary. The baseline remains 28 tables with its original SQL/catalog identity.

T018 tests three memberships for the same Telegram identity, independent language/block/marketing settings and wallet ownership, every foreign-brand flow-order pairing, same-brand other-member order ownership, foreign listings and rollback. Contract tests reject forged brand selectors inside callback envelopes at the existing authenticated brand boundary. Actual Telegram source verification, sender/private-chat proof, callback parsing and operation authorization remain T022; these tests do not qualify a live Telegram router.

T019 qualifies membership uniqueness/immutable identity, positive customer destinations, separately scoped negative-ID channel metadata, absence of a telegram_sessions relation and exact single-revision flow transitions. These are additional assertions over the reviewed baseline, not new DDL.
