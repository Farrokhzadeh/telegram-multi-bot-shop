# T081 AccAbad staging canary

**Status — 2026-11-18:** one-brand synthetic catalog canary prepared. Startup remains on hold. Reviewed repository head `a4eb3ab8e7061bb1e4ba67002d5ad5fd3fa0c152`.

## Candidate and boundaries

| Input | Pinned identity |
|---|---|
| Compose | [staging-canary.compose.json](compose/staging-canary.compose.json), SHA-256 `7ec8711cd74bfbffc405de25b8e922cea0e109058f5600aee4ca294b8ed41735` |
| Service artifact | Retained T076 Linux x64 Native AOT `Platform.Api`, SHA-256 `1dd09070d6ac6339987014d72d1f6572874b0380377039690fde582566df97c5`. T075 source inputs reused under the [T076 qualification](PERFORMANCE_EVIDENCE.md#t076-bounded-synthetic-qualification--2026-11-16). |
| Runtime image | Local Linux amd64 image `sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e` |
| Database image and fixture | Local Linux amd64 PostgreSQL 18.6 image `sha256:bd5dfa494a5a259704365813dbfa5fa6b3e69be7348d43b547f540c730478583`. The guarded 512 MiB T076 copy retains the pinned synthetic schema `d8a8a0cf1a42012a0ade2524671d5af85852c95740e92144aba83369efaf617e`. |
| Role and secrets | Only `stage-bot-a` and its dedicated PostgreSQL process. The protected T075 identity supplies the deployment UUID, brand keyring, database password and callback fixture credential. |
| Isolation | Separate internal Docker network, no published ports, no live supplier/payment/Telegram transport, no jobs actor, no production stock or data. |

The Native AOT executable and fixture are a matched historical pair for catalog callbacks. The newer managed T079 wallet checkout and T080 rollback qualify their own retained artifacts. They provide supporting evidence for behavior and recovery planning. Current-main build promotion, a wallet checkout on this pinned artifact, and production rollout each require their own qualification.

## Preflight and release decision

Docker Compose v5.3.0 rendered the exact candidate on accabad-server. Rendering failed with the required deployment identity absent. With the protected identity supplied, the output matched the two-service allowlist, the pinned local images and executable hash, the synthetic DB/schema binding, the single-brand keyring without an inventory key, and the protected certificate with more than 24 hours remaining. The app mounts are read-only, the database volume is declared external, and no ports are published. Both services carry the measured 0.1 CPU limit, 55 process cap, equal memory/swap limits and two 1 MiB log files. The original staging project was stopped, the bounded image detached and the canary project absent at preflight. Isolated operator tests verified that insufficient disk is rejected before mutation, that canary start selects the database then bot-a, and that stop selects bot-a then the database and clears owned state. The real container lifecycle has yet to run for this candidate.

Host preflight found 1,690,267,648 bytes free, about 1.57 GiB. The existing guarded route requires at least 3 GiB free, 1 GiB available RAM and two available CPUs. RAM and CPU bounds passed. Disk must pass before activation. The E6 operator decision is separate from preparing the manifest. No services were started, no volume was attached and no external transport or GitHub Actions was used during preparation.

## Verification after separate staging authorization

1. Recheck protected source and destination identities, artifact and manifest hashes, TLS validity, and the existing guarded headroom limits. Require the existing staging project stopped and its bounded copy exclusively reserved to this canary. Use `bounded_stage.py up FILESYSTEM_UUID --canary` from the reviewed checkout to bind the capped external volume and select only the canary services. The operator verifies the pinned manifest and reserves the loop state against the wider staging suite.
2. Use the same candidate to start PostgreSQL, check its deployment and schema markers, then start only `stage-bot-a`. Run a certificate-verified readiness request and one authenticated catalog callback with the synthetic brand and tester. Confirm the expected activity record and zero new orders. Reject a foreign-brand request.
3. Stop the canary bot first and confirm the database alone remains responsive, then stop PostgreSQL. Verify all canary actors stopped and the original staging and production containers retained their preflight states. Use `bounded_stage.py down` to detach the copy and clear the guarded state. If state needs restoring, use the retained synthetic backup and verify the expected identity and fingerprints before restarting any writer. The database and receipt restore and exclusive writer recovery evidence is in [T080](T080_STAGING_REHEARSAL.md#staging-closeout--2026-11-18).

Keep T081 unchecked until the applicable candidate checks and explicit release decision are recorded. Production promotion and T057/T091 supplier gates remain separate.
