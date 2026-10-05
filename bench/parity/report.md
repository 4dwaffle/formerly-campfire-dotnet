# Rails parity verification

**Verdict: FAIL for both .NET JIT and Native AOT.** The port is not a drop-in replacement for the pinned Rails Campfire application. Working benchmark paths and green port tests do not establish full feature or contract equivalence.

Audit date: 2026-10-05. Reference: Rails commit `6c7f8fa15f8c39478af64a483dcdf6223527ed22` and image digest `7197fff46e15d0dce69e0a16624df239e1b9bd069534dc28dec67ab7b6771672`. Runtime fixtures use the Rails-generated seed with separate disposable volumes for identity, chat, realtime/media, and browser/operations. Exact image IDs and fixture labels are retained in each area's `runtime/instances.json`.

This audit changes verification tooling and documentation only. Application source is unchanged from the benchmarked source: SHA256 `05cb09a1fb71b91740b3cf9f35d4787e1529ff3a15fcac57fcfbba88a8122e7c`, verified in [source-proof.json](source-proof.json). Reference checkouts were read-only. Nothing was committed, pushed, published, or sent to anyone. Stored external webhook/push subscriptions were removed from disposable runtime copies; integration tests use local receivers.

## Highest-priority confirmed failures

| Priority | Reproducible difference | Consequence | Evidence |
|---|---|---|---|
| P1 | Rails accepts the port's authentication token on GET, but rejects its encrypted session/CSRF cookie on mutation. Port session cookie is URI-escaped twice. | Switching from .NET back to Rails breaks existing mutation sessions. | [Identity audit](identity/report.md), captured cross-runtime cookie requests and Rails decryption result |
| P1 | Fresh .NET schema has one migration row instead of Rails' 15, and empty internal metadata. Rails lists 14 migrations DOWN. | Normal Rails db:prepare on a .NET-created database is unsafe; an early migration uses force:cascade. Destructive preparation was not executed. | [Identity audit](identity/report.md), fresh schema/migration inventory |
| P1 | Image analysis failure occurs after the .NET upload transaction commits; failure cleanup deletes the file. | Persisted attachment points to a missing file. | [Realtime/media audit](realtime_media/report.md), isolated fault reproduction |
| P1 | Missing first-run user parameters create account/user/room in .NET while Rails creates none. | Invalid setup request changes persistent state. | [Identity audit](identity/report.md), before/after database counts |
| P1 | .NET editor emits hostile attachment content rather than rebuilding Rails' sanitized editable attachment; opengraph literal-IP/same-host images survive .NET rendering. | Security-sensitive HTML/URI behavior differs. Browser script execution was not established. | [Chat audit](chat/report.md), raw editor/rendered HTML captures |

P1 is an audit triage priority, not a claim that an externally exploitable vulnerability was demonstrated.

## Coverage and outcome

| Area | Verification performed | Outcome |
|---|---|---|
| Identity, account, users, bots, persistence | Original Ruby/controllers/models/tests; live Rails/JIT/AOT request and mutation cases; cross-cookie directions; fresh installation and schema/migration inventory | Fail: parsing, validation, boolean casts, CSRF acceptance, session encoding, settings UI, migration metadata |
| Rooms, messages, boosts, search, rich text | Source/test inventory; live permissions, pagination, bot API, Turbo/JSON negotiation, rendering and transaction effects | Fail: raw bot forms, missing parameters, formats, conditional caching, mention/editor/attachment rendering, selected transaction effects |
| Realtime, storage, integrations | Source/channel/job inventory; live HTTP and Cable probes; actual media/variants; local fault and webhook receiver tests | Fail: presence lifecycle, origin checks, storage aliases/globs/HEAD, content identification, analysis ordering, integration behavior; process-local delivery and non-durable jobs |
| Framework, PWA, assets, browser, operations | 21 named HTTP probes; real browser login/history/message submission; both compiled asset manifests; online backup and isolated restore; route export | Fail: 12/21 probes differ on status/MIME, plus body/header/UI/asset differences. Browser submission and normal backup/restore pass |
| Existing port tests/build | Release build; all test projects and three executable protocol suites | Pass: 28 tests and three suites; zero build warnings/errors. They do not cover the differential failures |

The full structural export includes 178 Rails route entries, of which 136 target an existing controller action; .NET exposes 93 endpoints/182 method-path pairs. [Route coverage](route-coverage.md) identifies candidate mappings, absent candidates, framework routes, and nonexistent Rails actions. Method and parameter presence does not certify permissions, status, format suffixes, filename glob width, rendering, or side effects. A Rails resource declaration with a nonexistent action is not counted as a successfully implemented feature.

## Other confirmed contract differences

- HEAD returns 405 in .NET for representative Rails GET resources. `.html`, `.json`, and `.turbo_stream` suffix support differs. Human message creation ignores content negotiation, including Rails' 406 cases. Rails may commit a message before returning 406, which the mutation audit records separately from HTTP status.
- Curl-default form-encoded raw bot payloads create messages in Rails but fail in .NET. Missing message parameters create blank messages in .NET instead of Rails 400.
- JSON profile fields are silently ignored by .NET. Missing account/profile parameters, long passwords, duplicate emails, restriction boolean values, bot webhook identity, and CSRF form/header combinations differ.
- Expiring crypto output matches the Rails golden vector under en-US, cs-CZ, and tr-TR but fails under th-TH and ar-SA calendar cultures. The local production-crypto probe records 3 matches and 2 failures; this was not repeated inside each container under those cultures.
- Autocomplete query/filter and malformed room identifiers differ; captured Rails cache revalidation returns 304 while .NET returns 200.
- Rails presence absent/present actions and expired unsubscribe state do not match .NET. Realtime and integration infrastructure uses process-local queues rather than the Rails Redis/Resque deployment.
- PNG bytes declared text/html are identified as image/png by Rails; .NET persists the declared text/html. Rails eagerly creates image variant records on upload, while .NET delays creation until thumbnail GET. Original framework representation aliases and nested filenames are not fully supported.
- Welcome/login/profile controls and accessibility differ. Real browser sending works in all three runtimes, but QR/share controls and several notification/settings controls are missing or have different HTTP actions.
- All 314 unique original Rails compiled asset paths return 200 in Rails; 136 return 404 in each .NET build. The port's own manifest has different compiled digests/vendor overrides: 313/315 paths serve their copied bytes; two .br files return 404. This is an exact original asset URL compatibility failure, not a claim of 136 absent logical features.

See feature reports for passing controls, request names, raw response files, database snapshots, exact source references, upstream defects, and deliberate differences. Deliberate changes such as stronger transaction atomicity still differ from Rails; this report does not recommend weakening safeguards to reproduce every upstream defect.

## Evidence and reproduction

- Identity documents 27 findings: 19 runtime/probe-confirmed and 8 source-confirmed with explicit limits. Its main differential has 39 named cases per implementation, edges have 12 non-setup named cases per implementation plus six cookie directions, and fresh-schema comparisons cover all three implementations. These counts are not independent bug counts.
- Chat retains 580 authoritative response captures across main, edge, and follow-up probes. The selected assertion set has 134 comparisons: 66 pass and 68 fail, or 33 pass/34 fail for each .NET build. Each assertion compares a specific captured Rails observation; related assertions can describe the same underlying issue. See `chat/assertions.json`.
- Realtime/media documents 18 difference groups, supported by 106 HTTP responses, 15 WebSocket handshake attempts, 66 recorded protocol packets, local failure reproductions, and source inventories.
- [Identity detail](identity/report.md), [chat detail](chat/report.md), [realtime/media detail](realtime_media/report.md), [framework/browser/operations detail](browser/report.md).
- Raw responses include actual status, headers, bodies, and measurements where relevant. Mutation evidence includes SQLite state/FTS/attachment changes; protocol evidence records received Cable frames and connection behavior. Historical chat runs with corrected fixture assumptions are explicitly identified in the chat report.
- Parent harnesses: `serve.py`, `framework.py`, `assets.py`, `backup.py`, `inventory.py`, and `route_inventory/Inventory.csproj`. Agent harnesses live under their area directories.
- All four disposable audit families were stopped and their generated containers/volumes/networks cleaned after captures. Additional fresh-install and restore families were also cleaned. Raw evidence remains on disk; the pre-existing local development instance was not stopped. [verification.json](verification.json) retains the machine-readable FAIL result.

Example, against new disposable fixtures:

```powershell
python bench/parity/serve.py --family browser --port 4430
# In another terminal, after runtime/instances.json is ready:
python bench/parity/framework.py # exits 1 for observed contract differences
python bench/parity/assets.py   # captures both manifest path sets
python bench/parity/inventory.py
```

`backup.py` verifies the fixed browser test marker and requires first sending `parity-browser-20261005` through each application's real composer. It is not a generic zero-setup backup test. Agent mutation probes require fresh disposable families; rerunning them against already-mutated fixtures is not an independent reproduction. Remove a previous family's `runtime/stop` before relaunch, and write that file to request cleanup when complete. Preserve its raw evidence before reusing an output directory.

## Limits on the conclusion

This is a broad source and differential audit with concrete failing examples, not a proof over every Rails state and deployment. Full parity is disproved; no percentage of behavioral parity is asserted. Real browser Web Push provider delivery, exhaustive browser/platform/Turbo Native behavior, multi-process Redis delivery, restart/retry durability, every media format/animation/PDF transformation, interrupted storage recovery, and all pathological concurrent mutations are not certified. Source-only differences and dormant Rails paths are distinguished from live failures in the area reports. TLS default behavior was not tested because disposable fixtures disable SSL. Valid QR rendering returned SVG in every runtime; an independent QR decoder was not exercised.

The existing performance results remain measurements of the audited source and their recorded workload. They must not be presented as benchmarks of fully equivalent Rails behavior until these failures are resolved and the differential checks pass.
