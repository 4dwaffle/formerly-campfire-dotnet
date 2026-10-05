# Local benchmark methodology

The harness measures actual Campfire HTTP responses and persisted writes. The [latest results](results/latest/report.md) compare .NET JIT, .NET Native AOT and pinned Rust. Full Rails parity remains uncertified; feature `STATUS.md` files and parity reports record the remaining differences.

## Prepare and run

Run from the repository root with Python, Git and Docker Linux containers available:

```sh
python bench/prepare.py
python bench/run.py --apps dotnet,aot,rust --reps 2 --duration 10 --warmup 4 --concurrencies 32 --server-cpus 20-23 --client-cpus 24-27 --routes room,messages,search --suites http --output bench/results/local-comparison
```

Preparation verifies the reference revisions in [manifest.json](manifest.json), prepares the pinned images and load generator, builds JIT and Native AOT from the same source, and creates a Rails-generated seed. Existing reference checkouts at other revisions cause preparation to fail. Completed seeds are reused. Each output directory must be new. Include `rails` in `--apps` to measure the pinned Rails implementation as well.

Every application/repetition starts with its own Linux Docker volume copied from the seed. SQLite and Active Storage remain on the Linux filesystem. The harness removes webhook and push subscription records from each copy, disables outbound .NET integration delivery, and obtains a real session cookie and CSRF token from the rendered room.

## Workloads and validation

| Workload | Request |
|---|---|
| Room | `GET /rooms/:id` |
| Pagination | `GET /rooms/:id/messages?before=:id` |
| Sidebar | `GET /users/me/sidebar` |
| Search | `GET /searches?q=coffee` |
| Health | `GET /up` |
| Message creation | `POST /rooms/:id/messages` with a real CSRF token |

`--routes` selects measured workloads; omitting it measures all six. All read-route captures and message-ID comparisons are collected even for a focused run. Responses use normal database and rendering paths. The harness saves actual identity and encoded response bodies, headers, sizes, hashes and message IDs. Gzip captures must decompress successfully and contain matching message IDs. Record sets are compared across applications; this does not prove identical Rails HTML or transaction side effects.

The command above uses two alternating repetitions, ten-second samples at 32 clients, and four-second warmups at four clients. Each repetition gets a fresh seed copy; workloads within it share the process and database. Gzip is requested by default. .NET fragment gzip is enabled by default; `--fragment-gzip 0` selects whole-response gzip.

HTTP samples require zero transport, unexpected-status and protocol errors. Message creation must increase both message and matching FTS counts by the successful warmup and measured posts. The Redis job snapshot reports backlog, not completed worker throughput.

Independently validate saved evidence:

```sh
python bench/validate_http.py bench/results/latest
```

The retained results include raw samples, latency distributions, statuses/errors, resource samples, HTML and encoded captures, exact images/source identities, and independent validation. Server logs and profiler traces are local ignored diagnostics.

## Interpretation

The latest run covers 2,416,828 measured responses with zero errors. JIT and AOT use the same application source and configuration. Rust differs in CSRF, validators, broadcast and job processing, so throughput compares implementations with different work. Matching message IDs does not certify sanitization, authorization or full Rails parity.

Server and client containers use separate CPU sets on one Docker bridge network. CPU affinity does not guarantee physical-core isolation. Docker Desktop virtualization and background containers affect the results; metadata records the environment. Builds and profiling must not overlap throughput measurement.

Latency is closed-loop with no coordinated-omission correction. Two short repetitions are local engineering measurements. Startup includes Docker launch/readiness polling; sampled working set can miss peaks. Image sizes include media tools and AOT debugging symbols.

## Build times

Build times have not been measured for the retained version. To collect them separately:

```sh
python bench/build_times.py --output bench/results/local-build-times
```

The helper measures one uncached and one unchanged cached complete image build for JIT, Native AOT and pinned Rust using an isolated BuildKit builder. It preserves shared caches, uses an ignored Rust context, retains raw logs and step durations, and removes its builder. Complete timings include downloads and image loading; compiler/publish steps are recorded separately. Run after throughput workloads to avoid contention.

## Supplemental protocols

The HTTP results do not measure sustained Cable or media throughput. Prepare the instrumented clients for a separate run:

```sh
python bench/instrument_loadgen.py
docker build -f bench/Dockerfile.loadgen -t campfire-dotnet-loadgen:cable-checked bench/.work/instrumented-loadgen
docker build -f bench/Dockerfile.client -t campfire-dotnet-upload-client bench
python bench/run.py --apps dotnet,aot,rust --reps 3 --duration 5 --warmup 2 --suites cable,upload --cable-clients 100,1000 --loadgen-image campfire-dotnet-loadgen:cable-checked --output bench/results/local-protocols
```

Instrumentation copies the reference load generator into an ignored workspace and verifies guarded edits without changing the reference checkout or timed HTTP loop. Cable validation requires all requested clients to connect, zero errors, accounted POSTs and delivery of every marked message to every subscriber. Failed or incomplete samples remain failures. Clients share one user's cookie and websocket compression is off; this does not represent distinct users. The upload client posts real JPEGs and validates the actual message attachment thumbnail's format and dimensions.
