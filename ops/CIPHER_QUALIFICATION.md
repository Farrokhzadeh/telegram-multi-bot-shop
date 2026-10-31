# T012 authenticated ciphertext

Qualified in CI run 41233255842 at source commit d2218bf233182c158ad6726a0653ecce3c7db6a2, tree d964a7169197addbbeb23b74d95e7e94b48de49e. No real keys/data, import, host activation or deployment.

SC02 is a binary BYTEA envelope: four ASCII magic/version bytes, one-byte ASCII key-ID length, key ID (1–160 bytes), 12 random nonce bytes, ciphertext, 16-byte AES-256-GCM tag. No plaintext or legacy fallback. External key-ID metadata must match the envelope exactly; one key is selected, never a trial of unrelated keys. Retained old keys allow historical decryptions; encryption always selects the immutable snapshot's current ID. Key ID remains available through SecretSnapshot's current-ID APIs for the separate database metadata column.

AAD is deterministic and unambiguous: SC02, one scope byte (1 brand, 2 inventory), 16-byte network-order brand UUID (zero for platform inventory), one-byte table-name length and ASCII table, 16-byte network-order row UUID, one-byte field-name length and ASCII field, one-byte key-ID length and ASCII key ID. Table/field names are reviewed repository constants, ASCII alphanumeric/underscore, at most 64 bytes. This binds ciphertext to its original scope/table/row/field/key. Revision/deployment are runtime authorization checks through T011 and deliberately are not at-rest AAD: restoring a reviewed keyring does not rewrite ciphertext identity.

BrandCipher requires a non-null trusted context; InventoryCipher accepts only an inventory item ID and requires independently authorized inventory key access. Bot snapshots cannot obtain it. Plaintext is bounded to 1 MiB. Inputs are copied before synchronous key callbacks; owned plaintext copies and failed decryption buffers are zeroed. Returned plaintext belongs to the trusted caller, who must clear it and never log it. Malformed/mismatched/authentication failures return fixed safe exceptions with no original inner exception or payload. This is a cipher primitive, not completed audited decryption, membership authorization, key lifecycle operations or migration.

Production encryption uses a CSPRNG nonce, with no caller nonce override. Operational per-key encryption budgets/rotation and protected restore evidence remain release duties; random nonces do not provide durable business idempotency or duplicate-delivery permission.

The shared managed/native probe decrypts an independently generated Python cryptography AESGCM vector using a public all-zero test key/nonce; the production encrypt API never exposes deterministic nonce injection. Vector plaintext is public. Tests cover current/old key selection, nonidentical randomized ciphertext, UTF-8/empty plaintext, every-byte alteration, every truncation, plaintext rejection, brand/table/row/field/key mismatch and independent inventory access. Real secret-file loading is reused with generated temporary keys; the golden key exists only in that disposable fixture. No new dependency or schema changes.

AAD golden inputs: brand aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa; row 11111111-1111-1111-1111-111111111111; table brands; field bot_token_ciphertext; key ID old. The independently computed envelope is embedded in tests/Shared/CipherQualification.cs. Framework reference: https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.encrypt?view=net-10.0.

## Observed evidence

[CI run 41233255842](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41233255842), job `124631647265`, passed all steps: 172 tests (96 unit, 46 integration, 30 contract), zero failed/skipped, zero warnings/errors, secret scan, locked restore/audit, formatting, Compose render and all five Linux native executables. The actual native cipher probe passed its independent vector and all scope/corruption checks. T012 is complete for this synthetic scope. E2/E3/E5/E6 remain unqualified; E4 remains limited to recorded probes.

| Native executable | SHA-256 |
|---|---|
| Api | `9c3be77d8c234a82717f79c3892b3c6f9b675f25c6149ca3b1c245002805ffaa` |
| BotHost | `409206dec5d1f0f109d9a5be83592773f93c30c3b01a211175eefaf26a0fb8fc` |
| JobHost | `07d0112434c8a94fc9cc1de3deb72da9e4ecf27423bf5ed97f17c53c55ec2bb7` |
| SchemaRunner | `502c38513e961faacae262eaf8188814153e469db436761c53f23c5417738d5e` |
| AotSmoke | `c5dcfc74670646aacb0b4169b101988b0cf67cbaf8d4f482c4b26fd9287e93ae` |
