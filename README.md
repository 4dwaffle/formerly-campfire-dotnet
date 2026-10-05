# Formerly Campfire .NET

An in-progress port of [ONCE Campfire](https://github.com/basecamp/once-campfire) to ASP.NET Core Minimal APIs on .NET 10.

The port uses SQLite, the Active Storage disk layout, Rails authentication formats, and the pinned Campfire browser assets. Remediation of the [original failing Rails parity audit](bench/parity/report.md) is recorded in the feature reports and `bench/parity/remediation/`. Full parity is not certified.

## Benchmarks

Median requests/second, measured on 2026-10-05 with 32 concurrent clients and four server CPUs. Each implementation ran twice for 10 seconds after a four-second warmup, using fresh copies of the same Rails-generated seed and gzip responses.

| Workload | .NET JIT | .NET Native AOT | Rust |
|---|---:|---:|---:|
| Room | 10,914.2 | 10,180.9 | 13,769.8 |
| Pagination | 17,061.8 | 15,535.7 | 14,654.5 |
| Search | 11,262.0 | 10,675.0 | 16,759.5 |

All measured responses completed without transport, unexpected-status or protocol errors. The Release build and 330 tests pass; live verification covers 47 codec controls and 120 page assertions. Full Rails parity is not certified: twelve [stored-body normalization failures](bench/parity/chat/hotpath-validation.md) remain. Rust performs different CSRF, validator, broadcast and job work, so these results compare implementations rather than equivalent runtime workloads.

Servers used CPUs 20–23 and clients 24–27 under Docker Desktop. Background containers were present; builds and profiling did not overlap measurements. Latencies are closed-loop measurements without coordinated-omission correction. [Raw results, ranges and response captures](bench/results/latest/report.md), [image and source identities](bench/results/latest/build.json), [HTTP validation](bench/results/latest/validation.json), and [live compatibility checks](bench/results/latest/live-pages.json).

Build times have not been measured for this version.

### Reproduce

Requires Python, Docker with Linux containers, Git, and the [pinned reference checkouts](bench/manifest.json).

```sh
python bench/prepare.py
python bench/run.py --apps dotnet,aot,rust --reps 2 --duration 10 --warmup 4 --concurrencies 32 --server-cpus 20-23 --client-cpus 24-27 --routes room,messages,search --suites http --output bench/results/local-comparison
```

The harness checks response status, message IDs, gzip payloads and validators, and records exact images and background containers. [Benchmark methodology](bench/README.md) and [benchmark evidence](bench/results/README.md).

## Run locally

Install .NET SDK 10.0.401 (or a newer 10.0 patch). From PowerShell:

```powershell
$env:SECRET_KEY_BASE = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(64)).ToLowerInvariant()
$env:CAMPFIRE_STORAGE = Join-Path $PWD 'storage'
dotnet run --project src/Campfire --urls http://127.0.0.1:5088
```

Visit `http://127.0.0.1:5088/first_run` to create the account and administrator. Keep `SECRET_KEY_BASE` stable across restarts. Existing Campfire data must use its original secret and a copy of its original storage directory; do not test an unfinished port against the only copy of an existing installation.

Image and video previews require `vips`, `ffmpeg`, and `ffprobe`. The Docker image includes these tools.

Fragment gzip with compression-plan reuse is enabled by default. Set `CAMPFIRE_FRAGMENT_GZIP=false` to select ordinary whole-response gzip. Docker images include the native zlib helper; platforms without it fall back to whole-response gzip. [Design and codec evidence](bench/parity/realtime_media/fragment-gzip-assembly-plan.md) and [actual JIT/AOT runtime checks](bench/parity/realtime_media/fragment-gzip-live-hotpath.md) record fresh CSRF masks, complete-body validators, independent gzip decoding and cache invalidation.

## Docker

```sh
docker build -t campfire-dotnet .
docker run --rm -p 127.0.0.1:5088:3000 \
  -e SECRET_KEY_BASE=your-persistent-secret \
  -v campfire-dotnet:/rails/storage campfire-dotnet
```

For a Linux x64 Native AOT image, use the separate Dockerfile:

```sh
docker build -f Dockerfile.aot -t campfire-dotnet:aot .
docker run --rm -p 127.0.0.1:5088:3000 \
  -e SECRET_KEY_BASE=your-persistent-secret \
  -v campfire-dotnet-aot:/rails/storage campfire-dotnet:aot
```

The regular image runs the .NET application with the JIT. The AOT image compiles the same source to a native executable and uses `runtime-deps`, without a .NET runtime. Both include libvips and FFmpeg. The AOT build currently copies debugging symbols into the image, so its image size is not a stripped production minimum.

Native compatibility uses Dapper.AOT to generate concrete SQL mappings inside datastore callbacks, source-generated `System.Text.Json` metadata for HTTP responses and Rails-compatible signed payloads, and ASP.NET Core's Request Delegate Generator for Minimal API handlers. SQL ID sets use a source-generated JSON array with SQLite `json_each`; they do not rely on Dapper's runtime list expansion. See [persistence status](src/Campfire/Features/Persistence/STATUS.md) for the interception checks and limitations. These changes also run in the current JIT build.

Kestrel serves HTTP on port 3000 inside the container. Configure `REDIS_URL` for the shared Cable bus and durable Rails-compatible job queue. Integration delivery defaults to enabled; explicitly set `CAMPFIRE_DELIVER_INTEGRATIONS=false` to hold outbound jobs while allowing internal jobs. Set `VAPID_PUBLIC_KEY`, `VAPID_PRIVATE_KEY`, and `VAPID_SUBJECT` for push delivery. The production HTTPS assumptions follow Rails; the local Compose setup uses `DISABLE_SSL=1` for loopback HTTP. Check the feature status files for current compatibility limitations.

## Development

`IMPLEMENTATION.md` records ownership and integration contracts. Each feature's `STATUS.md` records implemented behavior, verification, and remaining gaps.

Run the functional checks from the repository root:

```sh
dotnet build Campfire.slnx -c Release
dotnet test tests/Identity/Identity.Tests.csproj -c Release --no-build
dotnet test tests/Chat/Chat.Tests.csproj -c Release --no-build
dotnet test tests/System/System.Tests.csproj -c Release --no-build
dotnet run --project tests/Storage/Storage.Tests.csproj -c Release --no-build
dotnet run --project tests/Realtime/Realtime.Tests.csproj -c Release --no-build
dotnet run --project tests/Integrations/Integrations.Tests.csproj -c Release --no-build
```

The last three projects are executable protocol checks. They use temporary SQLite databases, local HTTP/WebSocket servers, and cryptographic fixtures; they do not send notifications or webhooks to external services.

The current implementation passes 330 xUnit cases: 44 Identity, 64 Chat and 222 System, including 205 independently exercised Linux native-codec cases. Additional executable storage, realtime, Redis and integration checks retain their separately recorded source/image scopes. The restored 314 pinned asset paths matched byte-for-byte across the recorded Rails, JIT and AOT snapshots; an independent QR reader decoded all nine short/long/Unicode SVG cases. Shared Redis transport and durable Rails job wrappers are implemented. Runtime parity and media evidence is recorded separately from unit tests; full Rails rich-text/media/HTML parity remains unverified. Detailed results are recorded in [chat](src/Campfire/Features/Chat/STATUS.md), [identity](src/Campfire/Features/Identity/STATUS.md), [storage](src/Campfire/Features/Storage/STATUS.md), [realtime](src/Campfire/Features/Realtime/STATUS.md), and [integrations](src/Campfire/Features/Integrations/STATUS.md).

Against a fresh running Docker instance on port 5090, `python tests/System/container-smoke.py` checks setup, profile upload, and native libvips generation of the original 512px WebP avatar variant.

## Attribution

Campfire, its frontend, fixtures, and reference implementations are MIT-licensed by 37signals and contributors. See `LICENSE` and `THIRD-PARTY-NOTICES.md`. This is an independent port, not an official Basecamp release.
