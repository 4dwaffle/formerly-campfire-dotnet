# Chat and presentation parity audit

Audited 2026-10-05 against clean pinned Rails commit `6c7f8fa15f8c39478af64a483dcdf6223527ed22`. This is an audit, not a parity certification. Production files and reference checkouts were not changed. JIT and Native AOT exhibit the same selected outcomes; both have material compatibility and presentation gaps.

## Evidence and counts

- [Source inventory](source.json): 210 original logical assets compared after the expected asset-URL compilation; 208 match, two JavaScript files differ. This does not establish all vendored dependencies match Rails versions.
- [Authoritative main captures](runtime/differential-03/comparison.json): 160 Rails, 159 JIT and 159 AOT responses, with raw response bodies, headers and SQLite snapshots. Counts include login/setup requests. One extra Rails conditional GET exists because only Rails supplied Last-Modified.
- [Focused edge probes](runtime/edges-01/summary.json): 24 responses per application, including actual bot create/update/delete, boost ownership, editor reconstruction, OpenGraph and injected room-update failure.
- [Read-only follow-ups](runtime/followups-01/rails/responses.json): 11 responses per application, including five login/home requests, conditional GET, subtype collection routes and unauthenticated query bot key.
- [Selected assertions](assertions.json): **134 assertions, 66 pass, 68 fail**; **33 pass / 34 fail per .NET runtime**. `python bench/parity/chat/assertions.py` exits 1 deliberately while the verified differences remain. These are selected semantics and status contracts, not exhaustive test coverage or a count of unique bugs.

The main comparison's 276 structural/header differences are observations, not 276 defects: whitespace, framework MIME parameters, absolute origins, modified message IDs and deliberately simplified DOM contribute. Findings below cite actual behavior instead.

Instances, images, volumes, seed labels and fixture credentials are recorded in [instances.json](runtime/instances.json). The disposable chat family is Rails port 4410, JIT 4411, AOT 4412. Outgoing webhooks and push subscriptions were removed by the parent from each clone, so this audit did not contact external services. Seed credentials and bot keys are test fixtures.

`differential-01` initially assumed all fixture messages belonged to watercooler and did not establish a usable CSRF token for the loner. These two mistakes were corrected; use `differential-03`, not those affected earlier results. Runs 01/02/03 used the same disposable volumes and retained their controlled mutations. The current fixture-to-room mapping and before/after snapshots are saved. The focused edge SQL is recorded per application in `runtime/edges-01/*/fixture-changes.sql`.

## Scope matrix

`M` means membership scoped; `A` means creator or administrator; `B` means authenticated bot with membership. Every declared Rails resource also accepts its normal optional format suffix. The parent independently inventories general format/HEAD routing; suffix differences relevant to chat are reproduced here.

| Route family / methods | Rails implementation and permission | .NET location | Audit coverage / result |
|---|---|---|---|
| `/` GET | Welcome#show: last room or welcome | ChatFeature:37 | Human/loner home and welcome captured; complete welcome selection/flash UI not asserted |
| `/rooms` GET; `/rooms/:id` GET, DELETE; `/rooms/:id/@:message_id` GET | RoomsController, M; destroy A; inaccessible show redirects with alert | ChatFeature:43–50,172; ChatStore:26,145 | Full/frame show, missing room, around, generic delete behavior captured; flash distinctions not asserted |
| `/rooms/opens` GET/POST; `/new`, `/:id`, `/edit` GET; `/:id` PATCH/PUT/DELETE | Inherited index; M for existing room, creator/admin for writes; active users granted on create/conversion | ChatFeature:54–97; ChatStore:107,124 | New/edit and create/update source reviewed; GET collection missing (405 vs302); subtype conversion not fully mutated |
| `/rooms/closeds` same verbs | M, A for writes, explicit selected users; creator not implicitly added | Same | Creation/members and failure transaction checked; form initially selects creator; no exhaustive participant permutations |
| `/rooms/directs` same declared verbs | Current user participates; existing exact participant set reused; participant may destroy direct | Same; ChatStore:118 | New/edit/create/reuse captured; subtype index405 vs302; subtype show405 vs pinned Rails500; direct deletion participant permutations not fully tested |
| `/rooms/:id/settings` GET | Declared route but controller absent in pinned checkout | ChatFeature:103 | **Rails500 vs .NET302 to edit**; cannot claim a working Rails settings action was omitted |
| `/rooms/:id/refresh` GET | M; created/updated since timestamp, Turbo stream output | ChatFeature:133; ChatStore:78 | Happy GET captured; boundary/negative timestamp and actual concurrent refresh application unverified |
| `/rooms/:id/involvement` GET, PATCH/PUT | M; four enum states, direct/shared default cycle differs | ChatFeature:109–120; ChatStore:155 | Hide/sidebar exclusion/restore and invalid enum captured; full browser notification state unverified |
| `/users/:user_id/sidebar` GET | Current user's rooms, regardless route user_id | ChatFeature:104 | Own sidebar works; other route ID200 vs .NET404; neither reveals another user's sidebar |
| `/rooms/:id/messages` GET/POST; `/:message_id` GET/PATCH/PUT/DELETE; `/edit` GET | M; A for edit/update/destroy; create missing room renders recovery composer | ChatFeature:123–132,182,213–260; ChatStore:55,164 | CRUD, pages, permissions, JSON/form parameters, attachment clearing, editing and snapshots exercised |
| `/messages/:id` GET/PATCH/PUT/DELETE, `/edit` GET | Same controller but RoomScoped still requires room_id query | ChatFeature:125,127,130,132 | Without room_id Rails404 vs .NET automatic room lookup200; no evidence of membership bypass |
| `/messages/:id/boosts` GET/POST; `/new` GET; `/:boost_id` DELETE | Message membership; booster owns destroy | ChatFeature:160–163; ChatStore:200 | Human boost creation/frame and bot ownership lifecycle tested; declared unused show/edit/update actions not implemented by Rails or .NET |
| `/rooms/:id/:bot_key/messages` GET/POST; `/:message_id` PATCH/PUT/DELETE | B; A for changes; raw UTF-8 body or attachment; JSON; count/next headers | ChatFeature:164–167,193,310 | Plain body lifecycle and guards pass; default curl body, JSON plaintext/timestamps differ; suffix404 vs Rails200 |
| bot message `/boosts` POST, `/:boost_id` DELETE | B; raw body, booster owns deletion | ChatFeature:168–169 | Plain-text create/delete and foreign boost404 pass; default curl raw body201 vs422 |
| `/searches` GET/POST; `/searches/clear` DELETE | Authenticated, reachable message FTS; recent searches per user | ChatFeature:141–153; ChatStore:85–105 | Search, edit then search, recents, punctuation/operator inputs captured; clear mapped/source reviewed, not mutated in this run |
| `/autocompletable/users` GET | Active users, optional M room scope, filter presence fallback, 20-row page | ChatFeature:154–159; ChatStore:214 | Scope permissions pass; `.json`, blank filter, malformed room differ |

Rails `resources` also declares actions whose controllers do not implement them, including generic room new/create/edit/update, message new, and boost show/edit/update. A declared route is not proof of a functioning action. No claim is made that .NET must invent these absent actions. Parent's general route inventory covers their dispatch/status details.

## Verified findings

Priority P1 denotes security-sensitive or normal-user/API functionality/data compatibility; P2 denotes other contract/behavior differences. These priorities do not imply a remotely demonstrated exploit.

### C01 — P1: editor receives unrebuilt attachment content

Rails [RichTextHelper](../../../upstream/app/helpers/rich_text_helper.rb#L14) rebuilds every attachment before editing, specifically addressing old Trix metadata, mention MIME types and handwritten content. .NET [ChatRenderer:140](../../../src/Campfire/Features/Chat/ChatRenderer.cs#L140) places the stored body directly into the editor value.

The `handwritten-content-edit` edge capture contains `parityPwn()` in an image `onerror`, plus `data-controller="parity-pwn"` and its action, inside .NET's decoded attachment content. Rails rebuilds its editor attachment from trusted fields and omits these. Message presentation in **both** is safely reconstructed for this fixture. **Browser execution of hostile editor content was not verified.** Escaping the surrounding HTML value alone does not establish safety after Lexxy consumes the nested content.

Reproduce with [edge_cases.py](edge_cases.py); compare [Rails editor](runtime/edges-01/rails/handwritten-content-edit.body) and [JIT editor](runtime/edges-01/dotnet/handwritten-content-edit.body). Required regression: real browser opens edit, attachment renders safely, round-trips the supported MIME/content, and no event/controller payload is activated. Raw markup divergence is already asserted.

### C02 — P1: link previews retain own-origin and literal-IP destinations

Rails [OpenGraph web_url/elsewhere checks:53–85](../../../upstream/lib/rails_ext/actiontext_opengraph_embeds.rb#L53) reject own hostname, escaped hosts, non-domain/IP forms, hostless URLs and unsupported schemes. .NET [RichText:93–98](../../../src/Campfire/Features/Chat/RichText.cs#L93) only checks absolute HTTP(S).

`create-same-host-preview` produces an own-origin room URL in .NET `<img src>`, while Rails drops it. `ip-preview-create` similarly leaves `http://127.0.0.1/image.png` and its link in both .NET runtimes; Rails retains safe title/description but omits link/image. This can make a reader's browser request authenticated same-origin URLs or local addresses. **No server-side SSRF or browser exploit is claimed.** Unfurl fetch safety is another agent's scope and does not sanitize handwritten stored previews.

Evidence: [Rails](runtime/differential-03/rails/create-same-host-preview.body), [JIT](runtime/differential-03/dotnet/create-same-host-preview.body), [IP edge](runtime/edges-01/dotnet/ip-preview-create.body). Required cases also include case/trailing-dot origin, percent escapes, integer/hex IP spellings and hostless HTTP URLs.

### C03 — P1: legacy/expired signed mention rendering loses the user

Rails [ActionText fallback:5,13](../../../upstream/lib/rails_ext/action_text_attachables.rb#L13) recovers trusted stored User attachment identifiers when old signatures cannot be verified. .NET [RichText:100–104](../../../src/Campfire/Features/Chat/RichText.cs#L100) only accepts current valid signatures.

The seeded Marshal-era `messages.mention_marshal` renders `Thanks David, that was from the old days.` in Rails and `Thanks , that was from the old days.` in JIT/AOT. The edit value also lacks rebuilt mention content (C01). This is existing-data compatibility, not a recommendation to accept arbitrary untrusted signed IDs in request authorization.

Evidence: [Rails](runtime/differential-03/rails/fixture-mention_marshal.body), [JIT](runtime/differential-03/dotnet/fixture-mention_marshal.body). Required regression: seeded legacy/current/expired mentions in presentation, edit/save and plaintext/indexing under rotated secrets, while authorization remains strict.

### C04 — P1: documented curl-style bot requests rejected

Rails [RawRequestBody:6](../../../upstream/app/controllers/concerns/raw_request_body.rb#L6) rewinds and reads raw bytes regardless media type. Bot message/boost controllers use it. .NET [Input.Read:338–347](../../../src/Campfire/Features/Chat/ChatFeature.cs#L338) consumes form content before the raw branch. `curl --data 'hello'` defaults to form media type: Rails creates message/boost201; .NET treats it as no raw content and returns422. Explicit text/plain bot lifecycle passes.

Evidence: `bot-create-curl-default` and `bot-boost-curl-default` in [main captures](runtime/differential-03/dotnet/responses.json); assertion expects201 and observes422. Required regression: raw UTF-8 strings with default curl headers, ampersands/equal signs, blank bodies, multipart attachment and explicit text/plain.

### C05 — P1: blank attachment update cannot detach an attachment

Rails strong parameters permit `message[attachment]=""`, causing Active Storage removal. .NET [Attachment:304](../../../src/Campfire/Features/Chat/ChatFeature.cs#L304) collapses blank and absent values to null; [ChatStore.UpdateMessage:185](../../../src/Campfire/Features/Chat/ChatStore.cs#L185) only attaches when a blob ID exists. Both PATCH302, but after a clear Rails image attachment disappears; JIT/AOT retain it.

Evidence: `clear-attachment-before`, `clear-attachment`, `clear-attachment-after` raw bodies. Required regression distinguishes absent/blank/valid/invalid signed attachment and asserts record/blob purge policy separately (storage audit).

### C06 — P1: missing message parameters create a persisted blank message

Rails [message_params:75](../../../upstream/app/controllers/messages_controller.rb#L75) requires the message object and returns400 if missing. .NET [CreateMessage:220](../../../src/Campfire/Features/Chat/ChatFeature.cs#L220) substitutes an empty body and commits a message/FTS/unread mutation with200 Turbo output. An explicitly present blank `message[body]` is accepted by both; this finding is the missing object distinction.

Evidence: [Rails missing-parameter response](runtime/differential-03/rails/create-empty-params.body), [JIT](runtime/differential-03/dotnet/create-empty-params.body), before/after-create SQLite snapshots. Required regression asserts400 and zero message/FTS/unread/room changes for missing object.

### C07 — P2: negotiation, suffixes and subtype collection routes differ

`.json` autocomplete/bot index are200 in Rails and404 in .NET. Original autocomplete JavaScript therefore needed a local override to use Accept headers instead. GET `/rooms/opens`, `/rooms/closeds`, `/rooms/directs` inherits Rails index302; .NET returns405. Without room_id, top-level message show404 in Rails and200 in .NET. `/users/other_id/sidebar`200 in Rails (current sidebar) vs404 in .NET.

Normal message create with Accept JSON commits then returns **406** in pinned Rails because no create JSON template exists; .NET returns200 Turbo. Normal message show with Accept JSON406 vs .NET200 JSON. Regular update JSON returns pinned Rails500 for missing template vs .NET200 JSON. `/rooms/:id/settings` Rails500 missing controller vs .NET302 edit; direct subtype show Rails500 vs405. These actual pinned failures are documented differences, not evidence that .NET omitted successful JSON/settings functionality.

Evidence: main `.json`/JSON cases, [follow-up statuses](runtime/followups-01/dotnet/responses.json); source mappings [ChatFeature:54–169](../../../src/Campfire/Features/Chat/ChatFeature.cs#L54). Required tests should define which pinned errors are deliberately retained or intentionally improved, then check methods, formats, status and media type together.

### C08 — P2: conditional GET lacks Last-Modified / IMS handling

Rails message index uses `fresh_when` [MessagesController:15](../../../upstream/app/controllers/messages_controller.rb#L15). .NET [Page:196–199](../../../src/Campfire/Features/Chat/ChatFeature.cs#L196) implements ETag/If-None-Match, which passes; it supplies no Last-Modified and ignores If-Modified-Since. A future IMS produces Rails304 vs .NET200 in the actual follow-up request. Exact ETag algorithms/bytes differ and were not required equal by the selected assertions.

Evidence: [Rails](runtime/followups-01/rails/future-if-modified-since.body), [JIT](runtime/followups-01/dotnet/future-if-modified-since.body), response headers. Regression: last-modified header, old/new IMS, precedence with ETag, empty page204, and updates/boost touches invalidating validators.

### C09 — P2: blank/malformed selector handling changes results

Rails uses parameter presence for autocomplete filter and validates room/paging anchors by scoped lookup. .NET [ChatFeature:156–157,322](../../../src/Campfire/Features/Chat/ChatFeature.cs#L156) selects empty filter over query and converts malformed numeric selectors to null. `filter=&query=da` gives Rails only David and .NET seven users; `room_id=garbage` gives Rails404 vs .NET200 globally scoped users; `before=garbage` gives Rails404 vs .NET200 last page. No forbidden room messages were returned in tested valid cross-room/inaccessible selectors.

Evidence: `david-autocomplete-empty-filter`, `david-autocomplete-invalid-room`, `david-message-page-invalid-before`. Regression: empty/whitespace/non-numeric/zero/foreign anchor and room values, including member restrictions.

### C10 — P2: richtext plaintext and presentation filters differ

Rails [RemoveSoloUnfurledLinkText](../../../upstream/app/helpers/content_filters/remove_solo_unfurled_link_text.rb#L5) suppresses the lone original URL when its preview replaces it. The controlled `solo-legacy-create` fixture displays only the preview in Rails, and extra URL text in both .NET runtimes. The seeded `solo_unfurl` alone did **not** establish this gap, since both showed extra text for that seed shape.

Bot update `<p>First</p><p>Second</p>` JSON plaintext is `First\n\nSecond` in Rails versus `First\nSecond` in .NET [PlainText:27–38](../../../src/Campfire/Features/Chat/RichText.cs#L27). Bot timestamps use milliseconds (three fraction digits) in Rails versus six in .NET [Iso:107](../../../src/Campfire/Features/Chat/RichText.cs#L107). Bot JSON HTML uses Rails `body.to_s`, but .NET MessageJson uses presentation processing, so exact HTML is also different.

Evidence: edge solo/create and bot-lifecycle-update captures. Regression: blocks/br/list/table/code/email/link auto-linking, attachment/mention plain text, solo Twitter URL normalization, exact JSON date/plaintext contract and FTS content.

### C11 — P2: room rename transaction differs on membership failure

Rails [ClosedsController:32–33](../../../upstream/app/controllers/rooms/closeds_controller.rb#L32) commits room update then revises memberships separately. .NET [ChatStore.UpdateRoom:124–143](../../../src/Campfire/Features/Chat/ChatStore.cs#L124) performs both in one write transaction.

Injecting a temporary BEFORE DELETE membership trigger (only the newly created test room) makes both return500. Rails persists `parity-chat-fault-after`; .NET retains `parity-chat-fault-before`; both keep two members. Full SQL and resulting row are saved. This is a proven failure-side-effect divergence. Stronger .NET atomicity may be intentional; reproducing weaker behavior should require an explicit decision.

### C12 — P2: error semantics differ for invalid enum and FTS syntax

Invalid involvement is pinned Rails500 vs .NET422; FTS query `OR` is pinned Rails500 vs .NET200 empty because .NET quotes reserved operators. Attempting shared-room destruction through the direct namespace is Rails302 with flash vs .NET404, with no deletion in either. Source: [ChatStore:85–88,155](../../../src/Campfire/Features/Chat/ChatStore.cs#L85), original controllers/models. These defensive improvements still disprove exact status/UI parity; they do not warrant introducing500 responses without a decision.

### C13 — P2: bot key query authentication differs

With no session cookie, GET the regular messages route with a valid `?bot_key=` is Rails403 (authenticated bot but that controller disallows bot access) versus .NET302 login (route-value-only key detection). The similarly named edge probe used a human session and cannot establish this; the authoritative case is `unauthenticated-bot-query-regular-route` in followups-01. Authentication implementation belongs to the Identity audit.

## Frontend/source-only findings and coverage limits

- Notification bell markup has one loading image in .NET [ChatRenderer:169](../../../src/Campfire/Features/Chat/ChatRenderer.cs#L169); Rails [bell partial:7–8](../../../upstream/app/views/rooms/involvements/_bell.html.erb#L7) includes loading plus hidden alert. Original notifications controller `#showBellAlert` toggles all bell images. The DOM mismatch is verified; unavailable-permission behavior in a real browser remains unverified. .NET's notice also omits Rails installation/browser/system guidance partials.
- Edited message editor omits original typing/submission data actions, direct upload URL and blob URL template (main `fixture-edit-mention_marshal`). Missing data-action is asserted; actual typing/Enter behavior needs a browser test.
- Room forms and message/room frame renders approximate original markup. Full and Turbo-Frame responses are saved; .NET frame layouts remove head/meta/nav/footer much more broadly, while Rails uses a Turbo-specific layout. Raw layouts are not byte equivalent. Parent browser audit confirmed sending in all three and identified missing QR/share controls; that evidence belongs to the parent report.
- Two JS overrides are documented with diffs in source.json: autocomplete Accept negotiation and clipboard URL handling. Original CSS/source asset bytes otherwise match for the 210-file compared subset after URL compilation. Controller-to-DOM behavior is not proven merely by copying JS.
- Source-only: direct group settings/name may include viewer differently (`ChatForms:12` vs original directs/edit); message room labels may exclude viewer differently (`ChatRenderer:105` vs original `_message` helper invocation); emoji classifier numeric ranges differ from Rails Unicode character classes (`ChatRenderer:187`). No targeted runtime fixture proves visible failures yet.
- Source-only: .NET sanitizer persists sanitized HTML at write time, Rails stores body before presentation filters. This matters for editor/FTS behavior and requires case-by-case tests; a different stored string is not automatically an unsafe response.
- Source-only: Rails FTS callbacks are after-commit (`Message::Searchable:5–7`); .NET message/FTS updates share a write transaction (`ChatStore:164–198`). No injected FTS failure was run; do not infer equal failure durability from equal successful row counts.

## Passing areas and remaining tests

Selected assertions verify normal room/message page statuses, standard ETag304, formatting/blank-body creation, own update/delete, closed/direct creation and direct reuse, involvement hide/restore, forbidden human foreign edits, nonmember page/message/autocomplete404, explicit text/plain bot CRUD, invalid bot keys/inaccessible room rejection, bot own-boost delete204 and other-boost404, and bot foreign edit/delete403. Presentation removes javascript URI, mouseover event and script for the controlled fixture in all three. Message and FTS total row counts remain equal after mutations in both .NET runtimes. These are selected cases, not universal permission or sanitization proofs.

SQLite snapshots additionally show recent connected Jason remains unread-null while disconnected visible peers become unread after the focused creates, and creator remains unread-null. This is recorded evidence rather than a complete automated matrix of visible/invisible/nothing/mentions/everything and connectivity expiry. Membership/room snapshots and raw search-after-edit responses are retained for inspection.

The equal-timestamp fixture probes are **not accepted as a passing or failing pagination proof**: direct SQLite textual datetime insertion interacts differently with ORM parameter serialization, producing different peer selections. Proper same-time fixtures must be inserted through each runtime's native model/provider serialization before asserting strict tie behavior. Ordinary timestamp anchor source rules and existing before/after IDs were reviewed; the scripts do not yet assert exact complete page IDs.

Unverified: concurrent message/room edits, reconnect/unread races, closed/open conversions and all permission revocations through live Cable, stamped updates/refresh boundary ordering, every Unicode/emoticon/autolink case, file input edit/save round-trip, editor malicious-content execution, mobile/offline/PWA/notifications, active storage embedded blob attachment tracking/purge, durable webhook/push effects and external integration callbacks. Other feature reports cover some of these; this report does not certify them.

## Reproduction

Use newly cloned disposable chat volumes with the parent seed and recorded labels. `edge_cases.py` adds fixed-ID timestamp fixtures and must not be rerun against an already modified clone. No benchmark loops are required.

```powershell
python bench/parity/chat/source_audit.py
python bench/parity/chat/differential.py --output bench/parity/chat/runtime/new-main --mutate
python bench/parity/chat/edge_cases.py --output bench/parity/chat/runtime/new-edges
python bench/parity/chat/followups.py --output bench/parity/chat/runtime/new-followups
python bench/parity/chat/assertions.py
```

The bundled Python executable was used on this Windows host because the python alias was unavailable. `assertions.py` intentionally reads the authoritative saved directory names above; change its roots when evaluating a new run. It performs no HTTP or SQL changes. Do not interpret failed parity assertions as failed infrastructure or discard the failing captures.
