# Deployed assembly-plan gzip checks

Frozen runtime source: `e2d3a6b2373625d956b72648f045835c4c112e9b0c31295a0a1ccaca3d40bf46` (851 non-Markdown runtime files). The observed workspace hash matches the frozen build manifest `bench/results/performance-diagnosis-20261005/hotpath-final-build.json`. Actual container image IDs were independently inspected and match:

| Implementation | Exact image ID |
| --- | --- |
| JIT `hotpath-jit` | `sha256:e546b2f839cf3c86a515820159a6ada192005773ad86db09c91bcf9f308f623a` |
| Native AOT `hotpath-aot` | `sha256:dd2f3284584cc4c0f97a04a0065d4fba2cc423d38b576e5a5ea14dace2fce13a` |
| Pinned Rails | `sha256:7197fff46e15d0dce69e0a16624df239e1b9bd069534dc28dec67ab7b6771672` |

**47 grouped controls pass across99 HTTP requests** (33 per implementation), with zero HTTP5xx and three expected foreign-session CSRF422 rejections. Both actual .NET containers emit each of these markers exactly once: native zlib, immutable cache reuse and assembly-plan reuse. This verifies the deployed Native AOT helper/managed-dynamic compression path and plan cache, rather than inferring them from builds or unit tests.

Every gzip response is independently decoded with Python gzip and zlib and checked against its trailer CRC/ISIZE. Decoded SHA256 matches the whole-body Rails ETag prefix. Original room message IDs agree, while rendered HTML is implementation-specific: the warm JIT/AOT samples each decode to404,437bytes and use30,644gzip bytes; Rails decodes to463,720bytes and uses44,350gzip bytes. These different page sizes are recorded, not treated as identical compression work.

The controls cover fresh request CSRF and ETags during warm reuse; rejection of a foreign-session token; a second viewer's separate token; previous-token validators; message create, touch/update and delete invalidation; empty HEAD bodies; six encoding-negotiation cases; and literal Rack conditional matching. A stable manifest exact original weak ETag yields empty304 without gzip on GET and HEAD; stripping `W/`, supplying a list or using `*` yields200. Dynamic room wildcard requests yield200 on all three deployments.

Portable evidence is `fragment-gzip-live-hotpath-proof.json`, including image checks, exact build-manifest SHA, source hashes, all counts and each cleanup check. Raw captures/headers and server logs are under ignored `runtime/hotpath-codec-final/checks-v7`. Earlier7702/e028 proofs remain separate and unchanged.

The codec is now enabled by default in this frozen image; the parent selected that default after its separate same-image HTTP comparison. This run explicitly sets fragment gzip1 for unambiguous backend verification. It contains no timing workload and makes no independent throughput claim. The final parent benchmark evidence remains responsible for performance conclusions.

Known boundaries remain: invalid gzip quality syntax follows the .NET provider rather than Rack's permissive parser; identity-preferred negotiation matches direct Rack but differs from the pinned Thruster front proxy. Their original source/runtime evidence remains in `fragment-proxy-vs-rack.json`. No disposable route emits response `Cache-Control:no-transform`, so that deployed integration branch is unverified here. These focused checks do not certify all framework, media or application behavior.

Fixtures use CPUs24–27, isolated cloned seed volumes and local Redis, with seeded external webhook/push endpoints removed and delivery disabled. All eleven owned application/Redis/volume/network resources were removed and checked absent; the launcher exited0. No production source, build, shared fixture, benchmark timing or publication was changed by this run.
