# Third-party notices

This project derives behavior, translated markup, frontend assets and compatibility data from the following MIT-licensed projects:

- [ONCE Campfire](https://github.com/basecamp/once-campfire), 37signals LLC.
- [Campfire in Rust](https://github.com/basecamp/once-campfire-rust), 37signals LLC and contributors.
- [Campfire in Go](https://github.com/basecamp/once-campfire-go), 37signals LLC and contributors.

Their source revisions are recorded in `bench/manifest.json`. Campfire's bundled browser dependencies retain their own license notices in the asset files. NuGet dependencies retain their own licenses.

The MIME compatibility catalog in `MarcelTables.cs` is generated from Marcel 1.1.0 bundled with the pinned Rails image. Marcel code is MIT licensed; its MIME data is derived from Apache Tika under the Apache License 2.0. See [Marcel licensing](https://github.com/rails/marcel/tree/v1.1.0) and [Apache Tika](https://tika.apache.org/).
