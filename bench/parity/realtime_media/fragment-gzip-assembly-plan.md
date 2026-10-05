# Compression assembly plan candidate

This followup preserves the initial and e028 codec/image evidence. Production now caches an assembly plan in addition to immutable compressed pieces. It keeps the exact decoded HTML and whole-body ETag contract; host negotiation and HEAD/304 handling are unchanged. The parent enabled the codec by default after the paired HTTP pilot; `CAMPFIRE_FRAGMENT_GZIP=false` selects ordinary gzip. Final-image runtime checks and the larger comparison are recorded separately.

## Identified cost and comparison with pinned Rust

The pinned Rust `crates/kit/src/deflater/splice.rs` assembles roughly one piece per cached message and explicitly documents that its pages omit per-request CSRF tokens. Its ETag hashes parts rather than the entire body. Rust's `Cargo.toml:95` selects zlib-rs with runtime SIMD detection. Those choices are read-only reference behavior, not replacements for Rails' fresh CSRF and whole-body SHA contract in this port.

Our actual capture has 686 slices across40 messages. The previous .NET codec visited its full masked dictionary descriptors and folded CRC for every piece on every request. A plan avoids those repeated operations. An ignored prototype reduced warm encoding from the earlier0.210ms to about0.138ms; reusing native contexts and allocating the final output once reduced allocation but only modestly improved time. Fresh platform-zlib compression of three dynamic pieces remained the main cost. Compressing those pieces with supported managed `DeflateStream.Fastest` independently, then capturing its raw sync-flush bytes, reduced the final warm median to0.045ms.

A whole-response libdeflate alternative was rejected: on this capture its lowest tested level took0.376ms, versus0.140ms for the same-run managed comparator. Its smaller payload did not compensate for its greater CPU cost. This experiment adds no production dependency. Raw five-round data and candidate stages remain in `fragment-gzip-micro-plan-*.json`.

## Plan safety and bounds

The key hashes immutable array identities/ranges and dynamic lengths. Exact ordered descriptor comparison resolves collisions. A plan retains only immutable token-free arrays, compressed static runs, dynamic lengths and first-alias slot indexes. It stores no dynamic owner, value, hash, CRC or compressed dynamic input.

Static runs can include fixed DEFLATE length/distance commands copying an earlier dynamic slot. A hit must prove that this request's copied slot has the same array owner and range as its first occurrence. Splitting an alias rejects that plan. Previously distinct aliases may merge safely because their fresh compressed bytes and CRC are still evaluated separately. Length/distance eligibility remains non-overlapping, at most32KiB, and at least3 bytes. Otherwise the dynamic slot is independently compressed again.

Immutable pieces retain the existing full masked-history proof: unknown positions are zero, while cached current input must contain no NUL. No match can cross an unknown zero position. A static NUL rejects the plan and uses the uncached existing fallback. Dynamic input, including NUL and Unicode, is independently compressed and never supplies a global dictionary.

CRC concatenation is linear. Each segment's CRC is multiplied by the shift operator for all decoded bytes after it and XORed. The plan precalculates constant contributions and XORs coefficients for repeated dynamic aliases. Warm encoding evaluates each coefficient with that group's fresh CRC. Out-of-window re-encodings remain the same bytes, so their CRC contributions use the same alias coefficient. The final trailer still contains the exact whole decoded CRC and ISIZE.

The configured managed cache budget is shared:75% for compressed pieces and25% for plans, with at most128 plans. Conservative cost counts complete retained owners, even when a small slice references them. Concurrent hits are lock-free; FIFO insertion/eviction is serialized. Native cold compression uses at most64 lazily allocated, exclusive SafeHandle contexts. Their process-lifetime idle buffers are separate from the managed cache budget (roughly16–17MiB at the full bound). A burst above64 leases waits; failed contexts are destroyed. Reset precedes every use, caller pointers are cleared after each call, and only token-free immutable input enters these contexts. Dynamic compressors are disposed within the request.

## Verification and measured boundary

Release build passes with zero warnings/errors. Linux `FragmentGzipTests` passes **205/205**, with no failures/skips. Added regressions cover plan reuse with fresh Unicode/NUL secrets, alias splitting/merging, CRC contributions, ineligible/out-of-window references, immutable version changes, shared cache budgets/eviction and concurrent context reset/request isolation. `fragment_native_context_proof.py` adds19 independent native ABI/reset/fault and gzip checks. A deliberately undersized native output errors with no exposed result; the failed context is destroyed before a fresh context succeeds. This script exercises native error handling; it does not inject that failure into the managed lease wrapper.

The exact404,478-byte capture decodes through independent .NET GZipStream, Python gzip and Python zlib decoders, with matching CRC/ISIZE. Candidate output is30,644bytes, SHA256 `8250d57580aa662a57776af4e99a5894d125dac81d1916ab4d7dbf565503acb9`; the initial28,673-byte wire output remains historical. The new output trades about2KiB of dictionary savings for substantially cheaper dynamic compression and is still smaller than ordinary gzip's36,250bytes.

The final micro warms both encoders for at least2seconds and3,000 calls (actual9,105), then alternates five500-call rounds with32 fresh request-owner variants on CPUs24–27:

| Comparator | Median ms/encode | Allocated bytes/encode | Capture gzip bytes |
| --- | ---: | ---: | ---: |
| Assembly plan + managed dynamic | 0.04503 | 73,634 | 30,644 |
| Managed whole response, coalesced32KiB | 0.14880 | 160,026 | 36,250 |

This is approximately3.30× cheaper encoding on one warm capture, **not a server throughput claim**. It excludes HTTP, rendering, SQLite, ETag hashing and transfer, and does not predict cold/churning/search layouts. Both compressors ran under the same microprocess/affinity; shared-host effects remain possible. The parent's [same-image room HTTP pilot](https://github.com/4dwaffle/campfire-dotnet/blob/5cdf8ad38543096935b9a98d7443f7aa1a94e93f/bench/results/hotpath-codec-pilot-20261005/report.md) alternates ordinary/plan gzip over two repetitions: median4,347.4→5,368.6requests/s, zero errors across97,231 measured and23,156 warmup responses. Both implementations use the same immutable candidate image/source; that source precedes the final host default change. This narrow pilot supports the chosen default but is not a full-route or runtime ranking. No reference source, shared host code or Docker file was changed by this worker.

Portable proof: `fragment-gzip-assembly-plan-proof.json`, `fragment-gzip-micro-assembly-plan.json`, `fragment-gzip-native-context-proof.json`. All six exact production codec file hashes appear in the final micro report. Earlier first-plan and pooled-prototype reports accidentally attributed hashes to the production paths; they are marked accordingly and are not candidate source identities.

## Subsequent deployed verification

The frozen `hotpath-jit` / `hotpath-aot` images now pass47 grouped controls across99 actual HTTP requests against pinned Rails, with zero5xx, three expectedCSRF422 and each native/cache/assembly-plan marker exactly once per.NET image. Full body-derived ETags, fresh tokens, viewer isolation and mutations pass. Source and actual container image IDs are verified; all eleven owned resources were removed. See `fragment-gzip-live-hotpath.md` and its portable JSON for exact immutable attribution. The parent enabled the codec by default after its separate same-image HTTP comparison; this functional run performs no timing.
