# T017 foundation checkpoint

Qualified the combined T008–T016 foundation at commit `f63ded5bcbe17e58afddf7901745249bf96f6880`, tree `884838d7cb3e2a8d410d3e5e519f30ff742d95aa`. No customer runtime activation, source import, real keys/data, live providers/Telegram, deployment or staging/production mutation is authorized by this checkpoint.

The qualification suite and native executable compose the actual baseline/operator, exact money/transactions, trusted bot contexts, service secret grants, ciphertext, diagnostics, typed jobs and independent administrator authority. Additional integration/native checks use a wrong inventory key under the same key ID, inverse brand-to-inventory ciphertext, and an Api snapshot that has no inventory grant. A live metadata query verifies exactly 28 tables, no JSON/JSONB/array columns (including domain bases) and the reflection-free JSON feature setting. EAV exclusion additionally rests on the unchanged reviewed canonical schema/catalog digest; a type query alone is not proof of business semantics.

A global admin is deliberately allowed to manage all brands, but an audit cannot misattribute a user to another brand. The new cross-brand audit test commits a mismatched audit and a local product write together and verifies PostgreSQL rejects/rolls back both. Existing tests continue to exercise wrong-brand contexts, missing/incorrect key scopes, revoked/expired admin sessions and two-channel staff binding, idempotent repeated writes, crashed/replaced leases and lost COMMIT acknowledgements. API probes retain ready=503, no ordinary live startup, private-safe diagnostics and denial of bot/customer claims at the admin boundary.

Schema and migration identity remain the canonical 28-table T008 baseline. No duplicate auth migration/table or customer business handler is introduced here. This stage closes only the tested offline foundation checkpoint, not E2/E3/E5/E6 or the full runtime/release gates. Actual protected key restore, legacy-data compatibility, host identity, resource/load, transport/auth rate limits, external signatures/provider semantics and operational promotion remain separate evidence.

## Observed evidence

[CI run 41238819442](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41238819442), job `124648182843`, passed all steps: 207 tests (101 unit, 64 PostgreSQL integration, 42 contract), zero failed/skipped, zero build warnings/errors, locked restore/audit, secret scan, formatting, Compose render validation and five Linux native executables. The initial run 41237689229 failed compilation because IntegrationTests omitted the shared SyntheticSecretFiles source; the qualified commit adds that explicit compile link. Native execution passed the independent inventory-key boundaries and 28-table/zero-JSON metadata checkpoint along with the previous cipher, sessions/audit/binding, outbox and lost-commit probes. T017 is complete for this offline scope.

| Native executable | SHA-256 |
|---|---|
| Api | `ddf05b8b201442a8ab2b69b1979bb778de62479de6569272624c9972a5b07f16` |
| BotHost | `7e9cbefd666314cc6ecdb96c1f4b5c873f0809022d33537ac0cf30235b3cf8f1` |
| JobHost | `7f6881d34f0d98545c57c7932aa93d2246486b5ba4988c5d8a346e75a9e8a738` |
| SchemaRunner | `3e83fe661f0bf489a72412b01237eb3df6bd772dabdbbfbe9a96bcba620b9e38` |
| AotSmoke | `7516c1e2a436dc791655ddc0aaecf4c7861038a8c322d2aa4b2be0969215aa6f` |

The branch `impl/005-secret-files` at f21a0f84a2fa161e836984e0787c107413528b8a was compared directly by file blobs/content with main. Its secret loader, configuration, synthetic fixture, exact-key-byte native checks and Compose changes are present. Differing files contain later implementation/evidence/dependency additions; no missing branch change requires cherry-picking. Divergent commit ancestry reflects prior publication/merge history and is not evidence of missing code. Local reconstructed main matches the exact qualified remote tree above. Fifteen local Bash tooling tests also passed.
