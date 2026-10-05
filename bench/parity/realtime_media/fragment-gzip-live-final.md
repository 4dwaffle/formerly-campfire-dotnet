# Deployed fragment gzip checks (final optimized image)

Runtime source SHA256: `e028436cf93f752167e32c8d666de67cd3170a9cb4a79a6170f0f3172827e89e` (850 non-Markdown runtime files). Image attribution is the parent's `bench/results/performance-diagnosis-20261005/rust-inspired-final-build.json`; the observed workspace hash still matches. Original source `7702ad59…` evidence remains in `fragment-gzip-live-proof.json` and `fragment-gzip-live.md`.

| Implementation | Exact image ID |
| --- | --- |
| JIT `rust-inspired-v2-jit` | `sha256:cab8ba57b16f22ee31782932cd3bfd1d3eb54e72dee757ac81bedabee5967a9d` |
| Native AOT `rust-inspired-v2-aot` | `sha256:f9654c77a5bbe4c35760595b611269c78e342fc2c8df85fde91ea57c6e411889` |
| Pinned Rails | `sha256:7197fff46e15d0dce69e0a16624df239e1b9bd069534dc28dec67ab7b6771672` |

**47 grouped controls pass across 99 HTTP requests** (33 per implementation), with zero HTTP 5xx and three expected foreign-session CSRF422 rejections. Both .NET images emit native-zlib and cache-reuse markers exactly once. Actual deployed Native AOT compression and cache reuse are therefore verified. Portable counts, hashes, image IDs and resource checks are in `fragment-gzip-live-final-proof.json`; raw captures/logs remain under ignored `runtime/rust-inspired-codec-final/checks-v7`.

Checks independently decode gzip using Python `gzip` and `zlib`, validate trailer CRC/ISIZE, match decoded SHA256 to the whole-body Rails ETag prefix, verify 40 original message IDs, fresh per-request CSRF tokens and validators, separate viewer tokens, HEAD bodies, encoding negotiation, and create/update/delete invalidation. The helper reuses only immutable message fragments; dynamic token bytes remain specific to the request.

Literal Rack conditional behavior is now verified on all three deployments. The stable `/webmanifest.json` response has the same body and ETag across requests. Its exact original weak ETag yields empty304 without gzip, including HEAD; removing `W/`, providing a list, or using `*` yields200. Dynamic room wildcard requests also yield200. This closes the initial-image wildcard mismatch without weakening the fresh-token checks.

The first stable-response probe assumed message JSON was supported; all three returned the expected406. That harness assumption and its raw responses are preserved in `fragment-gzip-live-final-route-assumption.json` and `checks-v6`. No production change was made for it.

Two observed encoding distinctions remain: `identity;q=1,gzip;q=0.5` is identity through original Rack/Puma and .NET but gzip through the pinned Thruster proxy; invalid `gzip;q=invalid` is gzip through original Rack/proxy but identity through .NET. Direct-vs-proxy source/runtime evidence is retained in `fragment-proxy-vs-rack.json` and its source excerpt. This is focused codec/conditional evidence, not full HTTP parity certification. The deployed `Cache-Control:no-transform` response branch remains unverified because no disposable route emits it.

The fixture explicitly enables `--fragment-gzip 1`, uses CPUs24–27, isolated cloned seed volumes and local Redis, and disables delivery after removing seeded outbound endpoints. All eleven owned application/Redis/volume/network resources were removed and checked absent. Shared benchmark fixtures and immutable images were untouched.

The codec remains opt-in and disabled by default. Initial controlled HTTP measurements showed a throughput regression; optimized warm micro measurements improved its own encode cost but still exceeded managed whole-response compression. These functional checks make no throughput improvement claim. The parent owns the final same-image timing comparison.
