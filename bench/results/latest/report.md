# Campfire benchmark results

Measured 2026-10-05: two alternating 10-second repetitions at 32 clients, four-second warmups, four server CPUs (20–23), and client CPUs 24–27. Full Rails parity remains uncertified.

| Workload | Clients | .NET JIT | .NET Native AOT | Rust |
|---|---:|---:|---:|---:|
| room | 32 | 10,914.2 (10,887.1–10,941.4) | 10,180.9 (10,125.2–10,236.6) | 13,769.8 (13,730.4–13,809.1) |
| messages | 32 | 17,061.8 (17,003.4–17,120.1) | 15,535.7 (15,366.1–15,705.3) | 14,654.5 (14,530.9–14,778.2) |
| search | 32 | 11,262.0 (11,095.5–11,428.4) | 10,675.0 (10,626.5–10,723.4) | 16,759.5 (16,574.4–16,944.5) |

Cells are median requests/second (minimum–maximum). Raw JSON includes latency, statuses, response sizes and resource samples.

All apps use fresh copies of one Rails-generated seed on Docker Linux volumes. CPU sets and exact images are recorded in metadata.json. Docker Desktop bridge networking is part of these measurements.

## Response work

| App | Room bytes / messages | Page bytes / messages | Search bytes / messages | Sidebar bytes |
|---|---:|---:|---:|---:|
| .NET JIT | 404,437 / 40 | 372,084 / 40 | 145,908 / 13 | 30,351 |
| .NET Native AOT | 404,437 / 40 | 372,084 / 40 | 145,908 / 13 | 30,351 |
| Rust | 416,134 / 40 | 383,844 / 40 | 149,623 / 13 | 30,763 |

Sizes above are uncompressed captures. Message ID sets match across the retained implementations using the Rails-generated seed. HTML differs between implementations; equal business records do not establish equal rendering work or full parity.

## Latency

| Workload | Clients | .NET JIT | .NET Native AOT | Rust |
|---|---:|---:|---:|---:|
| room | 32 | 2.63 / 11.14 | 2.88 / 11.57 | 2.26 / 4.20 |
| messages | 32 | 1.82 / 3.64 | 1.99 / 4.67 | 2.12 / 3.87 |
| search | 32 | 2.81 / 5.15 | 2.97 / 5.56 | 1.79 / 4.09 |

Latency cells: median p50 / median p99 in milliseconds. These are closed-loop load measurements, without coordinated-omission correction.

## Container footprint

| App | Container start to healthy (ms) | Peak working set (MiB) | Image size (MiB) |
|---|---:|---:|---:|
| .NET JIT | 1115 | 117.7 | 1114.9 |
| .NET Native AOT | 1005 | 80.8 | 992.7 |
| Rust | 404 | 26.5 | 238.0 |

Startup includes Docker launch and readiness polling, not just application initialization. Working set is the median of per-run peak Docker stats samples. Images include media tools. CPU affinity does not isolate other host workloads; background containers are recorded in metadata.

## HTTP error counts

| App | Measured responses | Warmup responses | Transport errors | Unexpected statuses |
|---|---:|---:|---:|---:|
| .NET JIT | 784,918 | 165,489 | 0 | 0 |
| .NET Native AOT | 728,060 | 161,206 | 0 | 0 |
| Rust | 903,850 | 201,485 | 0 | 0 |

[HTTP validation](validation.json), [build and image identities](build.json), [live codec checks](live-codec.json), and [live page checks](live-pages.json). Build times have not been measured for this version.
