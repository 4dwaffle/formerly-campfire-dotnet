# Fragment gzip proof

The encoder is an optional response optimization. Host negotiation, full-body
Rails ETags, HEAD, 304, `no-transform`, content type and permissions remain owned
by the compatibility middleware. These size experiments are not server timing
benchmarks and do not establish a requests-per-second improvement.

## Implementation

`FragmentGzipEncoder.TryEncode` emits one gzip member: header, raw DEFLATE
fragments at `Z_SYNC_FLUSH` boundaries, one final empty block, combined CRC32 and
ISIZE. Native compression uses independent zlib streams; cached fragments use
level 6 and uncached dynamic bytes use level 1. If the helper cannot be loaded,
the encoder uses a whole-response managed `GZipStream`. Small responses return
false for the host's ordinary compression path.

The per-request history tracks the final 32KiB of immutable slices and lengths
of dynamic gaps. Dictionary bytes for gaps are NUL. Dictionaries are used only
with NUL-free input: a match can therefore never include an unknown gap byte,
and all known bytes retain their exact distance in the decoder's actual history.
NUL-containing input uses no dictionary and is never globally cached.

Only entirely token-free, immutable byte owners may be marked `Cacheable`.
Every segment, including dynamic segments, must remain unchanged during one
encoding call. Chat's encoded message owners meet this precondition; it creates
fresh immutable owners when the Rails message version changes. No byte hashing
or mutation checking is performed on warm fragments.

Cache keys contain current owner/slice identity and a fingerprint of history
owner/slice descriptors and dynamic **lengths**. Exact descriptor comparisons
resolve fingerprint collisions. Hits are lock-free. Inserts use bounded FIFO
eviction and `Lazy` compression outside the insertion lock. The default budget
is 64MiB/4,096 entries; accounting reserves compressed output, metadata and full
retained owner lengths, deduplicated only within each entry. No global cache key,
dictionary or compressed fragment contains CSRF values or dynamic shell bytes.

Repeated identical dynamic slices can reference their preceding occurrence only
within the same encoding call. Fixed-Huffman copies require owner and slice
identity, length >=3, a non-overlapping preceding occurrence and distance <=32KiB.
Long matches are split into legal length codes. Invalid copies fall back to
native compression. Local keys/checksums/encoded bytes disappear with the call.

CRC length operators are precomputed with the reflected CRC32 polynomial; warm
assembly combines checksums without decompressing, rereading or hashing cached
bytes. This follows zlib's CRC concatenation algebra, also used by the pinned
Rust `kit/src/deflater/splice.rs`; it does not adopt Rust's parts-based ETag.

## Evidence

The existing captured room at
`bench/results/render-cache-20261005/dotnet-1-room.html` has 404,478 UTF8 bytes,
40 messages and 686 reconstructed segments. Its SHA256 is
`a3439ac51991fb826db76662a7fe52ea50a006e306fa5e4b1b36609280bb803e`.
The fixture reconstructs token-free message owners and dynamic shell/token
segments; it does not classify the user-specific shell as cacheable.

`fragment-gzip-design-proof.json` records the rejected last-stable-piece design:
88,187 bytes even with local token references. `fragment-gzip-full-history-proof.json`
records Python full-window proof: 28,672 bytes and 315 independent decoder checks.
The actual C helper/C# encoder produces **28,673 bytes**, compared with **36,250**
from the installed .NET whole-response fastest compressor (ratio 0.791).
Python's own whole-response zlib produces 29,475 at level 1 and 20,417 at level 6;
these are different compressor/backend comparisons and are recorded separately.

`fragment-gzip-native-proof.json` records actual native cold/warm identical
output, 362 cached pieces, 14,387,043 reserved bytes, and exact .NET `GZipStream`,
Python `gzip` and Python `zlib` decompression, including CRC/ISIZE validation.
Raw gzip is kept under ignored `runtime/fragment-gzip/native.bin`; the report
contains only hashes, lengths and verification results. No secret bytes appear
in portable reports.

`tests/System/FragmentGzipTests.cs` covers managed fallback, NUL exclusion,
same-length varied secrets, changed immutable versions, owner/slice identity,
full-history and 32KiB boundaries, RFC1951 length/distance transitions,
ineligible-copy fallback, Unicode/empty segments, concurrent request isolation,
and owner-aware bounded eviction. Require the native path during Linux runs:

```sh
cc -O3 -Wall -Wextra -Werror -shared -fPIC \
  -o /tmp/libcampfire_compression.so \
  src/Campfire/Features/WebSupport/native/fragment-deflate.c -lz
LD_LIBRARY_PATH=/tmp CAMPFIRE_REQUIRE_NATIVE_COMPRESSION=1 \
  dotnet test tests/System/System.Tests.csproj -c Release \
  --filter FullyQualifiedName~FragmentGzipTests
LD_LIBRARY_PATH=/tmp dotnet run -c Release \
  --project bench/parity/realtime_media/fragment_fixture/FragmentFixture.csproj -- \
  bench/results/render-cache-20261005/dotnet-1-room.html \
  bench/parity/realtime_media/runtime/fragment-gzip/native.bin \
  bench/parity/realtime_media/fragment-gzip-native-proof.json
```

The fixture requires an existing raw capture. Unit tests do not depend on ignored
reference checkouts or captures. Native AOT-safe generated interop is implemented,
but these SDK-host tests alone do not prove a live Native AOT backend. Final image
and HTTP gzip/ETag/HEAD/304 negotiation verification belongs to the parent run.

## Followup CPU diagnosis and narrow fixes

The initial deployed codec is functionally verified separately in
`fragment-gzip-live.md`, including actual Native AOT backend/cache logs. The
parent's same-image on/off HTTP pilot found a throughput regression, so fragment
gzip is now opt-in (`CAMPFIRE_FRAGMENT_GZIP=true`); no speedup is claimed from
smaller output alone.

An instrumented ignored source copy identified **687,200 allocation bytes per
response** inside generic history equality (`fragment-gzip-micro-stages.json`).
Cache entries stayed362 before/after, and native compression was called three
times per response. The simulator preserves the one shared CSRF owner within
each request and changes only dynamic owners across requests; this was not a
false warm-cache or token-aliasing diagnosis. Instrumented timings are not CPU
comparisons; the stage probe is used only to locate allocations.

The final narrow changes compare descriptor identity/ranges explicitly, reuse
copy-command bytes by `(length,distance)` within each request, use value-type
piece metadata, use branchless CRC polynomial multiplication, and update a
descriptor-sum fingerprint incrementally on history append/remove/trim. The sum
does not encode ordering: exact ordered descriptor comparisons still resolve
every collision. A new reordered-history collision regression proves the check
is necessary and preserves correct decoding. Final native tests:197 passed,
zero failures/skips. `fragment-gzip-native-optimized-proof.json` confirms exact
initial compressed SHA256/bytes and independent decoder/CRC results.

Stable encode-only micro tests warm both compressors for at least two seconds
and3,000 calls, then alternate five500-call rounds using32 fresh dynamic-owner
variants. The raw baseline, each intermediate candidate and final stable rounds
are retained in `fragment-gzip-micro-*.json`, with exact codec file hashes.

| Warm encode-only comparator | Median milliseconds per response | Allocated bytes per response |
| --- | ---: | ---: |
| Initial fragment codec | 0.499 | 1,029,497 |
| Final narrow fragment codec | 0.210 | 308,225 |
| Coalesced whole-response managed gzip (final run) | 0.135 | 160,026 |

The fragment implementation improves its own CPU and allocation cost, but remains
slower than ordinary compression in this isolated encode micro. This excludes
HTTP, hashing, SQLite, rendering and network transfer; it is not a server RPS
result. Parent final-image on/off pilots determine deployment defaults. The
original image/source/raw measurements remain preserved and are not relabeled.

## Assembly-plan followup

The subsequent production candidate is documented separately in
`fragment-gzip-assembly-plan.md`, with its205 native tests, independent decoders,
bounded plan/native-context lifetimes, exact file hashes and fresh-token proof.
Its final encode-only median is0.045ms versus0.149ms for the same-run managed
whole-body comparator. This supersedes the CPU design but preserves the original
image and micro evidence above; deployment defaults await new HTTP measurements.
