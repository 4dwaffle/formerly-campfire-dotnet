# Framework, assets, browser, and operations parity

Date: 2026-10-05. Verdict: **FAIL** for both current JIT and Native AOT images.

Evidence is against the pinned source and images in `bench/manifest.json`. Disposable family identity, exact image IDs, labels, and start times are in `runtime/instances.json`. The parent audit did not change application source. All database writes below affected disposable copies only.

## HTTP observations

`../framework.py` captures actual status, headers, complete decoded response body, and SHA256 in `results/{rails,dotnet,aot}.json`; `results/comparison.json` summarizes 21 named probes. The script also captures login/CSRF requests and an authenticated HEAD request. Status/content-type agreement alone is not a pass on response behavior.

| Probe | Pinned Rails | JIT and AOT |
|---|---|---|
| GET /up | 200, exact 73-byte health HTML | Same bytes and status |
| HEAD /up, /session/new | 200 | 405 |
| OPTIONS /session/new | 404 HTML | 405 empty body |
| GET /session/new.html | 200 login page | 404 |
| Chrome 119 login, unique query to avoid frontend cache | 200 unsupported-browser page | 200 ordinary login page |
| /webmanifest, Accept text/html | 406 | 200 manifest |
| /webmanifest.json or JSON Accept | 200 application/json | 200 application/manifest+json |
| /service-worker, Accept text/html | 406 | 200 JavaScript |
| /service-worker, JavaScript Accept | 200 JavaScript | 200 JavaScript; semantic parity not exhaustively verified |
| Valid URL QR request | 200 SVG | 200 SVG; different rendering, decoding not independently verified |
| Invalid QR segment %%% | 400 text/plain | 422 empty body |
| Unknown route | 404 Rails error HTML | 404 empty body |
| /robots.txt and /404.html, /422.html, /500.html, /502.html | 200 static file | 200, but bytes differ |

The old-browser probe originally hit cached login HTML at the same URL. A distinct query yields the expected Rails browser gate; the retained final probe uses that query. Source: `upstream/app/controllers/concerns/allow_browser.rb`, `upstream/app/controllers/pwa_controller.rb`, `upstream/app/views/pwa/manifest.json.erb`, `upstream/app/controllers/qr_code_controller.rb`, and `src/Campfire/Features/WebSupport/WebSupportFeature.cs`.

Rails manifest asset URLs are absolute `image_url` values; .NET uses relative paths. The generated manifests otherwise use the same fixture account name and main fields. Byte equality is not required for this URL representation difference, but the negotiation and MIME differences above are real contracts.

The captured Rails health response includes `X-Frame-Options: SAMEORIGIN`, `Referrer-Policy: strict-origin-when-cross-origin`, `X-Permitted-Cross-Domain-Policies: none`, an ETag, and private revalidation cache headers. The .NET response lacks those headers and adds version/revision headers on health. See raw response headers. Rails CSP and Permissions-Policy initializers are commented examples, so their absence is not counted as a Rails gap.

Source-only deployment differences: Rails defaults to force/assume SSL when DISABLE_SSL is blank; Program.cs has no corresponding default redirect. These instances use DISABLE_SSL=1, so default TLS behavior was not exercised. Version fallback values also differ when APP_VERSION/GIT_REVISION are absent; these instances configure both as parity.

## Browser verification

The integrated browser used distinct hosts `rails.localhost:4430`, `jit.localhost:4431`, and `aot.localhost:4432` to isolate host-scoped session cookies. Real user actions logged in as the fixture administrator, opened All Talk, filled the Lexxy message textbox, and clicked Send Message. Every implementation displayed exactly one `parity-browser-20261005` message. Full resulting accessible DOM snapshots are in `results/*-room-dom.txt`.

Observed presentation gaps: .NET welcome omits the join-link QR link, Share join link button, translation control, and invite textbox accessible name. Group direct-room titles use `J+J+K` instead of Rails `J, K, and J`. Initial login markup lacks the Rails account logo, administrator mail contact, and version footer. Identity/chat reports cover further settings and HTML differences. Working composer submission does not establish all editing, media, push, accessibility, browser/platform, or Turbo Native behaviors.

## Asset compatibility

`../assets.py` fetched all unique paths in both compiled manifests. `results/assets.json` checks the port's 315 paths: 313 returned their copied bytes in JIT and AOT; the two `.br` Lexxy files are present locally but return 404. These are not demonstrated browser module failures: normal .js assets work.

The running Rails compiled manifest has 314 unique paths, all served successfully in Rails. Both .NET builds return 404 for 136 of those exact original digested paths, recorded in `results/original-assets.json`. Of the port's own paths, Rails returns 404 for 135. This reflects different compiled digests/vendor assets and the retained Rust overrides documented in the chat STATUS, rather than 135 missing logical application features. However, original cached-page URLs are not preserved, so asset contract parity fails. No exhaustive semantic comparison of all JavaScript/CSS or importmap bindings is claimed.

## Backup/restore controls

`../backup.py` invoked each image's real pre-backup hook against its running disposable database, then copied that family's storage to a new isolated volume and invoked the real post-restore hook. All prepare/restore commands exited zero. Every snapshot and restored database passed `PRAGMA integrity_check`, retained 170 messages, and the snapshot FTS retained the one browser-created message. Raw commands, stdout/stderr, and cleanup results are in `results/backup.json`. SQLite immutable mode is used only to inspect offline snapshots on read-only mounts; an initial ordinary SQLite inspection could not create WAL/shared-memory files on that mount and was corrected.

This verifies normal production.sqlite3 backup/restore with no concurrent writes during snapshot assertions. Non-production RAILS_ENV handling differs in source: Rails uses the environment-specific filename, .NET hardcodes production.sqlite3. Disk corruption, interrupted backup/restore, and filesystem failure recovery were not tested.

## Structural inventory and existing tests

`../rails-routes.json` exports 178 route entries; 136 point to an actual controller action. `../dotnet-routes.json` exports 93 ASP.NET endpoints (182 method/path pairs). `../route-coverage.md` lists every route and candidate mapping. Some Rails resource routes target nonexistent actions; framework routes may have environmental restrictions. Candidate matching deliberately does not certify behavior. Rails filename globs are broader than the single-segment .NET storage filename routes.

`dotnet build -c Release`: passed, zero warnings/errors. `dotnet test -c Release --no-build`: 28 passed (13 identity, 13 chat, 2 system). The three existing executable Storage, Realtime, and Integrations suites also passed. Those green tests coexist with the differential failures above; they do not prove full parity.
