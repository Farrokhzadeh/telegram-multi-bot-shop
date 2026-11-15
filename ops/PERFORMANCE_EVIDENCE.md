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

## Native API continuation — 2026-11-15

**Status: still NOT QUALIFIED.** This continuation supersedes the earlier API build result only. T076 and T080 remain unchecked.

The current source inputs are unchanged by the intervening documentation-only commit `7351cf64b8dc20bc65114f512cd7c0a470f568cb`. Further 896 MiB, 0.5 CPU attempts included an explicit garbage-collection heap cap and an isolated single-threaded compiler; the latter again ran out of memory writing its object file. Removing the native compiler's `-g` option and running it separately with size optimization, one compiler thread and conservative garbage collection produced a 50 MiB native object (SHA-256 `94bd1eb68c9e796b11e3195b366c52c2dcd2a6230fb23882a4bdd0c0b7988ab3`). The modified compiler response file's SHA-256 is `67c003e46d3674319cddafdf461e83e88cd621387ef7a03048c6bfe3322f491e`. Stack-trace data remained enabled. A direct native linker run used the recorded SDK link command without its debug flag and produced the API ELF SHA-256 `1f23ba0c6d1fb6bd00be4b1ecad88a5bcff303c65ed4348d6d5a3b9b9c816622`. This separately compiled and linked artifact is a qualification artifact; it is not an immutable release build.

On the recorded T075 runtime image with no network, read-only mounts, 0.18 CPU, 112 MiB and 55 processes, its synthetic `--qualify` returned success with applicationReady=false; unconfigured normal startup returned 78. With the existing staging database and API only, a temporary Compose override mounted the native artifact read-only and selected `--serve-synthetic-staging api`. Identity, stopped state, project labels, mount boundaries and unpublished ports were checked before start. The isolated database became ready and the native API returned its readiness response.

A separate resource-limited client on the private staging network sent 100 certificate-verified HTTPS readiness requests, all successful. During one load sample, Docker reported API 1.76% CPU and 24.56 MiB, database 0.03% CPU and 33.25 MiB; these are sampled container metrics, not peak RSS or a representative checkout workload. An independent API stop/start reached its readiness response in 2.973 seconds measured from the Docker start command to the successful loopback probe, including orchestration and polling. One sample is not a distribution or a comparable legacy startup measurement.

The native API and staging database were stopped independently afterward; all 13 legacy containers were still running. The original managed API container configuration was restored in the stopped state. No real external service, production data or production writer was used. The protected build and runtime checkpoint remains on the owner server.

The comparable legacy baseline, representative target workload, peak RSS, resource headroom and bounded shared-server limits remain unqualified. Readiness traffic alone cannot establish them. An automatic review rejected an attempted legacy configuration inspection because the requested fields could reveal secrets; no such configuration was displayed. A safe synthetic baseline remains to be established without exposing configuration. The remaining-work list above continues to apply, except that the native API now has the limited results recorded here.
