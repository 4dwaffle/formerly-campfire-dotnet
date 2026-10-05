# Benchmark results

[Latest .NET JIT, .NET Native AOT and Rust results](latest/report.md), measured on 2026-10-05. This directory contains only the latest measured implementation versions.

- [Raw run metadata](latest/metadata.json): settings, source identities, exact images and environment.
- [Build manifest](latest/build.json): measured executable identities. Build times have not been measured for this version.
- [Independent HTTP validation](latest/validation.json): 2,416,828 measured responses with zero errors, response hashes, gzip decoding and message IDs.
- [Live codec checks](latest/live-codec.json) and [live page checks](latest/live-pages.json).

The per-application JSON files and HTML/encoded captures retain the actual measurements and response bytes. Runtime source hash: `4ba40a663875c3e16521faaac3e22f66c4ac27b81ed356a258a2f99620031f9a`. Source hashes describe exact build-workspace bytes; Git line-ending normalization can change a checkout's byte hash without a code change. Image IDs identify the measured executables.

Full Rails parity is not certified. Twelve [stored-body normalization failures](../parity/chat/hotpath-validation.md) remain. See the [methodology](../README.md) for workload and comparison limits.
