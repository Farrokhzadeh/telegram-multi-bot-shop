# T076 performance evidence

## Partial native-host checkpoint — 2026-11-15

**Status: NOT QUALIFIED.** T076 remains open. These results do not establish application readiness, a representative legacy comparison, production headroom or release readiness. T080 has not been started in this checkpoint.

The starting main commit was `a3cde4ca3d310aa8d02d2811a4c8f42064bd0f17`. The isolated build workspace was copied from the T075 deployed synthetic staging workspace, whose protected identity manifest identifies that commit. A comparison with the earlier source manifest found 403 matching inputs; the five changed tracked inputs and the added staging source were independently matched to main's Git blob IDs. This is a checkpoint identity check, not a new full-source or artifact promotion review.

Builds used the existing Linux x64 .NET SDK 10.0.403 native compiler image `sha256:6c0c55c45706c3bf953321e8e44396fea1966352e73ed0df7f4b41c8c7b588b6`, the existing locked package cache, no network, one build at a time, 0.5 CPU, 896 MiB memory and no swap. The successful runtime probes used the T075 Linux x64 runtime image `sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e`, no network, read-only mounts, 0.18 CPU, 112 MiB and a 55-process limit.

| Executable | SHA-256 | Observed scope |
|---|---|---|
| Platform.BotHost | `8b05db9f6a8254fbc4c7c1de6cb82896bf4f55cd8c385c32ce2c8752adadedeb` | Published native ELF; explicit synthetic qualification returned success and reported applicationReady=false; normal startup exited 78. |
| Platform.JobHost | `5ef1825acfc1b1e0bed3b1a56ecf38f9b7fa0fcd90c7da8f4f055a42553d4c52` | Published native executable; explicit synthetic qualification returned success and reported applicationReady=false; normal startup exited 78. |
| Platform.SchemaRunner | `59e2d25c7786e72ff35f9ad57d47a842e06f32daed7e70e430c7ce4052f62e54` | Published native executable; invocation without an explicit operation and protected inputs exited 78. No database operation was exercised in this checkpoint. |
| Platform.Api | Not produced | Three native publish attempts failed in the native compiler with insufficient memory at the 896 MiB limit: default, explicit single-threaded compiler, and single-threaded size optimization. Docker did not report an OOM kill; the compiler returned an error. No API native runtime result is claimed. |
| Platform.AotSmoke | Not attempted in this batch | The sequential batch stopped on the API failure. T096's earlier focused native preorder probe retains only its recorded scope. |

The first bot build lacked restored assets; an offline locked restore corrected that. A subsequent attempt was intentionally stopped after detecting that its SDK image lacked the native compiler; the successful bot build used the native compiler image above. The API retries preserved the limit instead of increasing pressure on the shared host. Each failed attempt and its logs remain on the owner server under the private T076 checkpoint. Build outputs and stopped build containers remain for identity review and resumption.

A read-only snapshot before the builds showed approximately 1.4 GiB available memory, 6.6 GiB free disk and 13 running legacy containers. Staging's six stopped containers had configured per-service limits of 0.18 CPU and 55 processes, 112 MiB for each of five service containers and 160 MiB for PostgreSQL; JSON logs were limited to two 1 MiB files per container. The T075 deployed synthetic qualification already exercised the independent project stop and observed the original running container set unchanged. This checkpoint observed all 13 legacy containers running after the attempts and the redesign staging project stopped. These observations are not a bounded-load, connection, disk-growth or production-reserve qualification and do not prove absence of transient interference.

## Remaining work

- Obtain a safely built current native API artifact, verify its identity and run its synthetic staging and fail-closed paths. Do not raise shared-host build limits without measuring a safe reserve; use a separate appropriately sized builder or another bounded method.
- Establish matching current evidence for every published executable and the appropriate native smoke scope.
- Measure startup, CPU and RSS under comparable synthetic legacy and target workloads, state the workload and limitations, then exercise staging resource and independent-stop controls under bounded load.
- Complete T076 only after its requirements pass. Then continue only the authorized synthetic or disposable-copy T080 backup and restore rehearsal. Provider identity, protected keys, writer ownership and release remain separately gated.
