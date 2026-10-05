# Deployed fragment gzip checks (initial image)

Frozen runtime source: `7702ad59794a99f7853827b97ae49924c71e0d9cdf20d1ff5f29a22a059b6ee5`.
The parent preserves its build manifest at
`bench/results/performance-diagnosis-20261005/rust-inspired-initial-build.json`.
Followup workspace changes are recorded separately from this immutable image.

| Implementation | Exact image ID |
| --- | --- |
| JIT `rust-inspired-jit` | `sha256:c453efc414c95bf62b360cbf3d18a926c3b098bf9e38a4dff9fc03488b8422df` |
| Native AOT `rust-inspired-aot` | `sha256:cf8530ecce2ed05dfb245ba032db90f158e9c2d24dfd3cbc0ec88e16296a1d08` |
| Pinned Rails | `sha256:7197fff46e15d0dce69e0a16624df239e1b9bd069534dc28dec67ab7b6771672` |

The final functional run verifies 38 grouped controls with 78 HTTP requests
(26 per implementation), no HTTP 5xx and three expected foreign-session CSRF422
rejections. Each .NET container emits the one-time native-zlib and cache-reuse
markers exactly once. This is actual deployed Native AOT interop/cache evidence,
not an inference from compilation or SDK tests.

Controls independently decode gzip with Python `gzip` and `zlib`, validate the
gzip trailer CRC/ISIZE, compare decoded-body SHA256 with the original whole-body
Rails ETag prefix, verify matching message IDs, fresh request CSRF and fresh ETags,
previous-token ETag200 behavior, HEAD/conditional body handling, six encoding
negotiations, separate logged-in user sessions, and message create/update/delete
cache invalidation. Captured responses/headers are under ignored
`runtime/rust-inspired-codec`. Portable counts/hashes and cleanup checks are in
`fragment-gzip-live-proof.json`.

Earlier probes contained incorrect harness assumptions (HEAD content encoding,
unscoped message mutations, input IDs mistaken for message IDs, and looking for
old text across an entire page containing prior test messages). Initial failed
proofs and raw attempts are preserved; the final run scopes checks to actual
message-root fragments. HEAD has a zero body and `Content-Encoding:gzip` on both
Rails and the full .NET middleware pipeline. The generic unit middleware test
does not include the outer compression middleware, so its header result alone
was not evidence for a live difference.

## Confirmed source/runtime distinctions

These controls do **not** certify full HTTP parity:

* The pinned Rack3.2.6 `ConditionalGet#etag_matches?` compares the ETag and
  If-None-Match strings literally. Original dynamic-room `If-None-Match:*`
  returns200; the initial .NET image returns304 (also on HEAD). The parent owns
  the followup literal-comparison fix; this report preserves the initial image.
* `identity;q=1,gzip;q=0.5` produces identity directly through Rack/Puma3001,
  but gzip through pinned Thruster0.1.23 on the published3000 port. The .NET
  provider selects identity. Exact sent headers and the direct-vs-front checks
  are captured in `fragment-proxy-vs-rack.json`; there are no duplicate header
  assumptions. Rack's source preference algorithm selects identity here.
* `gzip;q=invalid` produces gzip through both direct Rack/Puma and Thruster;
  the .NET provider selects identity. `gzip;q=0` produces identity everywhere.
  Selected pinned gem source excerpts, paths and line numbers are in
  `fragment-proxy-vs-rack.source.txt`. The parent retained standards negotiation
  rather than copying a proxy-specific preference quirk without broader scope.
* No fixture route emits response `Cache-Control:no-transform`; that deployed
  integration branch is unverified by this run.

All fixtures use CPUs24–27, isolated seed clones and separate local Redis.
Seeded webhook/push endpoints are removed and delivery is disabled. All eleven
owned app/Redis/volume/network resources are removed; the portable report records
each check. No shared seed, benchmark image or parent fixture was changed.

The parent's controlled initial-image benchmark shows native fragment gzip slower
than ordinary compression despite smaller wire bytes. This report is functional
evidence, not a throughput improvement claim. Followup encode-only micro baselines,
allocation-stage probes and any optimized-image verification remain separate.
