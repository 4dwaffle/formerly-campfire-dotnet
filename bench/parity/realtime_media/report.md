# Realtime, storage and integrations parity audit

**Result: full Rails parity is not established. Eighteen difference groups below contain confirmed runtime differences, isolated failure reproductions and source-confirmed architectural differences. No production fixes were made.**

Audit date: 2026-10-05. Rails application revision `6c7f8fa15f8c39478af64a483dcdf6223527ed22`; reference checkout was clean. Framework files were read from the pinned running Rails image, Rails framework revision directory `rails-1a02651ac37f`. Application/image identities and disposable volumes are recorded in `runtime/instances.json`; extracted framework file hashes are in `runtime/framework/inventory.json`; feature source hashes, route/channel/job methods and line numbers are in `inventory.json`.

Runtime contexts: Rails `127.0.0.1:4420`, JIT `:4421`, Native AOT `:4422`, each with a separate cloned seed volume. The fixtures' webhooks/subscriptions were deleted by the launcher. A temporary subscription registration used the deliberately invalid keys from the original Rails test; it was removed before message creation. No push notification was delivered and no external HTTP endpoint was contacted. Webhook reproductions use a separate local Kestrel stub. Source/reference files remained read-only.

## Evidence and scope

The final captures contain **106 HTTP responses**: Rails36, JIT35, AOT35, including real multipart PNG/PDF/MIME-spoof uploads, actual thumbnail bytes, direct upload integrity failures, HEAD and Range requests. The probes performed **15 WebSocket handshake attempts** (five per implementation): nine handshake-header blocks are retained, with eight first welcome packets and one denied-origin attempt; the six subsequent channel connections retain **58 protocol packets**, for **66 recorded packet objects** altogether. SQLite snapshots retain before/after presence and thumbnail state. `probe.py` and `media_probe.py` retain complete response bodies with HTTP status/headers/body hashes; retained WS headers redact session cookies. These are functional checks, not performance measurements.

Evidence levels used below:

- **Live3**: independently observed against all three cloned contexts. JIT and AOT agreed on these checks.
- **Local**: isolated .NET failure/behavior reproduction using real SQLite/files or local HTTP; Rails comparison is explicitly supported by source/tests, not a claimed live cross-runtime test.
- **Source**: control flow/configuration establishes a difference; operational consequences not load-tested or crash-tested here.

The source audit covers every executable channel and all application/Active Storage jobs, plus their configuration/hooks. Native live checks cover typed JSON serialization, SQL materialization, Cable welcome/confirm/reject/typing/disconnect, storage allocation/token validation, image analysis/thumbnail processing and upload MIME behavior. They do not certify every native integration, cryptographic provider or media codec.

## Confirmed differences

| ID | Evidence | Trigger and result | Source evidence |
| --- | --- | --- | --- |
| RM01 | Live3 | `HEAD /account/logo`, signed user avatar and signed blob proxy: Rails200 with headers/no body; JIT/AOT405. Storage routes only register GET, unlike Rails implicit HEAD. | `src/Campfire/Features/Storage/StorageFeature.cs:16-33`; `upstream/config/routes.rb:24-27,43-45`; framework `activestorage/config/routes.rb:5-14`. Captures `runtime/*/results.json`, labels `*_head`. |
| RM02 | Live3 | Legacy `/rails/active_storage/representations/{signed}/{variation}/{file}` and filenames containing `/folder/audit.png`: Rails302; JIT/AOT404. The corresponding legacy blob alias is also absent in source. Rails uses `*filename`; the port uses one `{filename}` segment and registers only explicit redirect/proxy paths. Optional Rails `.:format` variants are not mapped either. | `StorageFeature.cs:21-28`; framework `activestorage/config/routes.rb:5-14`. `runtime/*-media/results.json`: `representation_legacy_alias`, `representation_nested_filename`. |
| RM03 | Live3 / Source | Form-encoded Active Storage direct upload allocates a blob and returns200 on Rails; JIT/AOT422 because they parse the body as JSON. Push registration has the same JSON-only parser despite the original controller's form/query support. | `StorageFeature.cs:146-171`; `IntegrationsFeature.cs:201-220`; framework `activestorage/app/controllers/active_storage/direct_uploads_controller.rb:6-15`; `upstream/app/controllers/users/push_subscriptions_controller.rb:32-33`; original controller test `upstream/test/controllers/users/push_subscriptions_controller_test.rb:9-30`. `direct_form` captures. |
| RM04 | Live3 | Upload actual PNG bytes declaring `text/html`: Rails stores `image/png` and width/height; JIT/AOT retain `text/html`, no dimensions, and set `identified=true`. The port trusts the submitted MIME and marks identification complete without inspecting bytes. This also permits a declared safe image MIME to reach libvips with different actual file bytes. | `StorageFeature.cs:73-74`; `MediaProcessing.cs:15-51`; framework `activestorage/app/models/active_storage/blob/identifiable.rb:12-32`. `runtime/*-media/results.json`: `spoof`. |
| RM05 | Live3 | After real 2×2 PNG multipart POST, before any thumbnail request, Rails has a variant record1, JIT/AOT0. GET creates the port record later; all return genuine 2×2 PNG bytes. Creation/broadcast behavior and transaction side effects therefore differ even when the eventual thumbnail works. | `upstream/app/models/message/attachment.rb:14-16,23-39`; `upstream/test/models/message/attachment_test.rb:7-15`; `StorageFeature.cs:99,117-120,127-131,373-425`. `before_thumbnail_fetch` / `after_thumbnail_fetch`. |
| RM06 | Local | Inject SQLite failure on the analysis metadata UPDATE. `SaveRecordUploadAsync` commits the new attachment/blob, analysis throws, and its catch deletes the new disk file. Reproduction confirms `attachment_committed=true`, `file_exists=false`. Replacing an old attachment can also detach the old record before this failure. | `StorageFeature.cs:88-102`; `MediaProcessing.cs:50-60`; audit `tests/Parity/RealtimeMedia/Program.cs:10-21`; `runtime/local-repro.json`. Rails normal attachment creation is transaction-aware and synchronous explicit `process_attachment` happens after creation; this exact injected Rails failure was not tested. |
| RM07 | Source, supported by live download headers | The port's dangerous-content list omits Rails binary-forced `application/postscript`, `application/x-shockwave-flash`, `application/mathml+xml`, `text/cache-manifest`. It also serves arbitrary MIME inline by default, whereas Rails permits inline only its configured image/PDF list. The real text upload proxy returned Rails `attachment`, port `inline`. Full proxy caching/ETag semantics differ: Rails immutable/http-cache-forever; port one-hour TTL/checksum ETag. | `StorageFeature.cs:219-253`; framework `activestorage/lib/active_storage/engine.rb:53-78`; downloaded text response headers in `runtime/*/results.json`. |
| RM08 | Source | Variant implementation accepts only `format` and `resize_to_limit`, max4096 dimensions, and output png/jpg/jpeg/webp/gif. Rails valid signed transformation hashes may contain other image_processing operations and formats. The port accepts either string-format or symbol-format variation digest as the same cached image; Rails exact Marshal digest distinguishes them. A single process-local semaphore serializes all transforms; cross-process unique variant races are not coordinated and can leave extra image blobs/attachments. | `StorageFeature.cs:133-139,300,373-425`; `RubyVariation.cs`; framework `activestorage/app/models/active_storage/variation.rb:19-82`, `blob/representable.rb`, `variant_with_record.rb:45-67`. No concurrent variant race or TIFF/HEIC encoder/default-format matrix was run. |
| RM09 | Source, runtime limitation explicitly checked | PDF preview support is absent from `Transform` (non-image/non-video rejected). Rails registers Poppler/MuPDF previewers. In this pinned running Rails image the uploaded PDF ALSO had preview_count0; therefore **no live PDF-preview difference is claimed**. Video analysis omits Rails angle/display-aspect-ratio and rotated dimension handling; audio analysis omits bit_rate/sample_rate/tags. The port CLI invocation does not apply Rails' `Vips.block_untrusted(true)`/OpenSlide loader block. | `StorageFeature.cs:373-385,409-410`; `MediaProcessing.cs:19-43,64-96`; `upstream/config/initializers/vips.rb:6-9`; `upstream/test/lib/vips_loader_policy_test.rb:63-106`; framework `active_storage/engine.rb:27-28`, video analyzer `:29-85`, audio analyzer `:21-40`. `runtime/*-media/results.json`: `pdf`. Malicious loader payloads were not exercised. |
| RM10 | Live3 | Public PresenceChannel `absent` sets Rails connections0/connected_atNULL; JIT/AOT ignore it and retain1. Calling `present` updates Rails state/broadcasts read; the port ignores it. `refresh` is the only implemented presence action after subscribe. | `upstream/app/channels/presence_channel.rb:5-17`; `RealtimeFeature.cs:125-136`; `runtime/*/results.json`: `presence_absent`, `presence_present`. |
| RM11 | Live3 | With count2 and connected_at70seconds old, unsubscribe yields Rails0/NULL, JIT/AOT1/stale timestamp. Ruby resets expired counts, the port decrements unconditionally. This can affect presence counts. The parent host DOES implement Rails' startup reset of currently connected memberships (`src/Campfire/Program.cs:38`), so startup reset is not a missing feature. | `upstream/app/models/membership/connectable.rb:12-13,34-50`; `upstream/config/puma.rb:50-51`; `RealtimeFeature.cs:156-160`. `presence_expired_unsubscribe` snapshots. |
| RM12 | Live3 | For HTTP server, WS handshake with `Origin: https://same-host:port`: Rails404, JIT/AOT101+welcome. Port compares only authority; Rails compares scheme and host. Both implementations accepted absent subprotocol with welcome, so absent subprotocol is **not** reported as a difference. | `RealtimeFeature.cs:40-43`; framework `actioncable/lib/action_cable/connection/base.rb:228-238`; `runtime/*/results.json`: `wrong_origin_scheme`, `no_protocol`. |
| RM13 | Source | Connections/broadcasts/remote disconnection are process-local dictionaries in the port. Rails Cable uses Redis pubsub with production prefix; membership destruction sends remote reconnection through Action Cable, including other Puma workers. Running multiple port instances cannot broadcast/revoke across instances. Bounded128 outgoing queue additionally cancels slow consumers; the same queue boundary does not exist in original Cable. Neither implementation is claimed to provide durable Cable replay. | `RealtimeFeature.cs:31,161-209,263-264`; `upstream/config/cable.yml:1-24`; `upstream/app/models/membership.rb:7`; `upstream/app/models/user.rb:48-62`; framework `actioncable/lib/action_cable/server/connections.rb`. Multi-process failover was not executed. |
| RM14 | Source | Signed nonguarded Turbo streams are narrowed to `rooms` and two own-user streams in the port; original Turbo accepts any verified stream except the guarded `:messages` suffix. Room signed-GID parsing validates a permitted type name then uses only numeric room ID, rather than Rails GlobalID locating the actual typed room. Changing a room STI type can thus have different stale signed-stream behavior. RoomChannel subscribes but the port has no general `broadcast_to room` delivery implementation for arbitrary RoomChannel payloads. | `RealtimeFeature.cs:105-113,141-151,161-184`; `upstream/app/channels/room_messages_channel.rb:21-37`; `upstream/app/channels/concerns/room_streams_are_authorized.rb:7-13`; `upstream/app/channels/room_channel.rb:2-5`. Malformed frame types may throw outside the port's JsonException-only handler (`:66,83-94`); fuzzing not performed. |
| RM15 | Source / local delivery controls | Production Resque schedules independent push and bot jobs, backed by Redis. Port combines them in volatile single-reader Channel256; queued work disappears on process loss, a bot failure can skip subsequent bots AND original message push, and work is entirely skipped when `CAMPFIRE_DELIVER_INTEGRATIONS` is unset/false. This is a deliberate operational difference, not equivalent job execution. Push pool scheduling also differs (Ruby50threads, port sequential). Rails application retry/discard directives are COMMENTED OUT; **automatic application-job retry is not claimed**. However framework Analyze/Mirror/Transform/Purge/PreviewImage jobs do have retry/discard policies absent from synchronous port methods. | `IntegrationsFeature.cs:43-58,83-101`; `upstream/config/environments/production.rb:96`; `upstream/config/initializers/resque.rb:1`; `upstream/app/models/message.rb:12`; `upstream/app/models/room.rb:83-84`; `upstream/app/controllers/messages_controller.rb:79-85`; `upstream/lib/web_push/pool.rb:5-8,25-41`; framework captured jobs. |
| RM16 | Local / Source | Attachment-only message webhook `body.plain` is empty in the port; Rails uses the filename fallback. Local200 `application/json` webhook reply creates0 port messages; Rails treats any recognized Content-Type as an attachment, not only image/video/PDF, and does not require successful HTTP status for that fallback. Port uses one total7s deadline vs Ruby7s open/read timeouts, UTF-8 decodes all text, and has a narrow extension map. Its16MiB cap is applied after `PostAsync` default response buffering, so does not bound the initial response read. Reply SQL/FTS/unread changes are atomic, but staging analysis failure RM06 can corrupt an attachment. | `IntegrationsFeature.cs:104-158`; `upstream/app/models/webhook.rb:29-34,41-46,57-78`; `upstream/app/models/message.rb:24-25`; `runtime/local-repro.json`. Ruby attachment fallback was source-verified; the cross-runtime webhook job was not fired. |
| RM17 | Live3 / Source | Original Rails test keys `p256dh=123`, `auth=456` register200; JIT/AOT reject422 because they require valid P256/auth keys. The port rejects endpoint userinfo/fragment and every address in mixed DNS answers; Rails vendor policy plus Surfguard accepts a public resolved address. Port invalidates410/404 only; Rails also invalidates OpenSSL errors. Test notification returns503 for disabled/unsafe/missing keys in the port; Rails always redirects after successful/no-op `.deliver` (exceptions may500). Badge evaluated during dispatch differs from Ruby notification construction before threadpool handoff. Port one-record crypto rejects plaintext>3993bytes; long push behavior is not proved equivalent. Push index omits original user-agent/browser/platform metadata. | `IntegrationsFeature.cs:161-182,189-235`; `OutboundPolicy.cs:11-66`; `WebPushEncoding.cs:17-40`; `upstream/app/models/push/subscription.rb:14-25`; `upstream/lib/restricted_http/private_network_guard.rb:24-26`; `upstream/lib/web_push/pool.rb:38-41`; `upstream/app/controllers/users/push_subscriptions/test_notifications_controller.rb:4-6`; `upstream/app/views/users/push_subscriptions/_push_subscription.html.erb`. `push_dummy_keys` captures. Real vendor delivery deliberately untested. |
| RM18 | Local / Source | Canonical fallback `https://www.example.com` becomes trailing-slash URL in the port; original preserves supplied string. Port uppercase `.JPG` is skipped, original case-sensitive regex would fetch. Media filename in query `?download=photo.jpg` is fetched by port (only AbsolutePath checked), original whole-URL regex skips it. URI normalization also affects canonical/image URL strings. HTTP/meta charset handling and timeout/proxy behavior differ (port12s total, no proxies; original Net::HTTP defaults, Nokogiri/meta encoding). Private/DNS-rebinding policies use hand-coded address classification vs Surfguard, with mixed-answer semantics above. | `OpenGraph.cs:19-55,64,81-99`; `upstream/app/models/opengraph/location.rb:14-15,31`; `upstream/app/models/opengraph/metadata/fetching.rb:5-8,36-47`; `upstream/app/models/opengraph/document.rb:24-25`; `upstream/app/models/opengraph/fetch.rb:30-44`; `runtime/local-repro.json`. No external unfurl requests were made. |

## Passing controls

These controls passed on the tested paths; they are not a declaration that the surrounding feature has full parity:

1. Browser login and actual seeded room responses succeeded on Rails/JIT/AOT.
2. Normal Cable handshake/welcome, seven app channel subscriptions, nonexistent room rejection and exact typing action/user payload worked. Presence subscription clears unread and broadcasts the correct `{room_id}` read shape.
3. Real signed RoomMessagesChannel subscription accepted; replaying the same room signature on stock Turbo rejected on all three. Actual multipart creation delivered a Turbo append targeting `messages_rooms_direct_186869642`.
4. Direct JSON blob allocation200, bad-content checksum422, valid signed disk PUT204, downloaded exact bytes200 all passed on three implementations. Auth/CSRF, purpose-bound signatures, seeded Marshal variation digests and purge behavior passed the independent existing Storage suite.
5. Actual multipart PNG creation200, native image analysis and followed signed thumbnail GET produced real2×2 PNG bytes on all three. The source/caching/creation differences are listed above.
6. Seeded signed avatar and logo GET returned200 on all three; fixed stock/custom routes and signatures function on these seeded cases.
7. Blank unfurl URL returns400 and deleting a nonexistent own push subscription redirects302 on all three.
8. Native responses on these paths had no reflection-disabled JSON/materialization exceptions and agreed with JIT; this cannot cover unexecuted branches/codecs.
9. Existing Realtime, Storage and Integrations executable suites passed again during this audit. They verify membership/guarded stream isolation, fresh-session revocation, deleted message UUID and `maintain_scroll`, disk integrity/ranges/purge, actual aes128gcm receiver decryption, VAPID signature/audience, safe vendor/address validation and opt-in local bot persistence/FTS/unread side effects. They disable JSON reflection. These suites are .NET behavioral controls, not Rails comparison suites.

## Complete route inventory in this feature slice

`inventory.json.rails_routes` has24 Rails route rows, including route declarations whose controller actions are absent; those absent actions are not counted as implemented Rails behavior. Parent-owned bot CRUD and bot message APIs are inventoried by Identity/Chat; their integration call sites were inspected here. HEAD is implicit for Rails GET and explicitly failed in tested port routes. Optional `.:format` and `*filename` shape differences are RM02.

| Executable Rails route | Port handler / disposition |
| --- | --- |
| `/cable` WS | `RealtimeFeature.MapRealtimeFeature` / `RealtimeService.ConnectAsync` |
| POST `/rails/active_storage/direct_uploads` | `MediaService.DirectUpload` (JSON-only difference) |
| PUT `/rails/active_storage/disk/:encoded_token` | `DiskUpload`, authenticated, signed token |
| GET `/rails/active_storage/disk/:encoded_key/*filename` | `DiskDownload`, public signed read, single filename segment |
| GET `/rails/active_storage/blobs/redirect/:signed_id/*filename` | `BlobDownload` → signed disk URL |
| GET `/rails/active_storage/blobs/proxy/:signed_id/*filename` | `BlobDownload` → file |
| GET `/rails/active_storage/blobs/:signed_id/*filename` | Missing legacy alias |
| GET `/rails/active_storage/representations/redirect/:signed_blob_id/:variation_key/*filename` | `Representation` → variant then disk redirect |
| GET `/rails/active_storage/representations/proxy/:signed_blob_id/:variation_key/*filename` | `Representation` → variant file |
| GET `/rails/active_storage/representations/:signed_blob_id/:variation_key/*filename` | Missing legacy alias |
| GET/DELETE `/users/:user_id/avatar` | `Avatar` signed URL / `DeleteAvatar` current user |
| GET/DELETE `/account/logo` | `Logo` public / `DeleteLogo` admin |
| GET/POST `/users/:user_id/push_subscriptions` | `Index` / `Subscribe`, current user regardless route parameter |
| DELETE `/users/:user_id/push_subscriptions/:id` | `Unsubscribe`, current user's records only |
| POST `/users/:user_id/push_subscriptions/:push_subscription_id/test_notifications` | `TestNotification`, current user |
| POST `/unfurl_link` | `OpenGraphService.Create` |

Rails also declares push `new/edit/show/update(PATCH/PUT)` via `resources`; its controller implements only index/create/destroy. Missing port versions are not an executable controller parity gap. The complete route dump records `implemented_action=false` for them.

## Complete channel inventory

Ten Ruby channel files comprise seven executable app channels, two bases and the Turbo guard concern; externally supplied Turbo::StreamsChannel is the eighth executable channel in scope.

| Channel | Rails behavior | Port/control status |
| --- | --- | --- |
| ApplicationCable::Connection | Cookie session → current_user, reject unauthorized | Browser identity required; fresh cached-cookie lookup per send/heartbeat is additional protection |
| ApplicationCable::Channel | Base | Combined RealtimeService |
| HeartbeatChannel | Empty subscription; connection-level ping | Subscribe/confirm and3s ping |
| RoomChannel | Member room, `stream_for room` | Subscription check; no generic room payload implementation (RM14) |
| PresenceChannel | Subscribe present; unsubscribe absent; present/absent/refresh callable | Subscribe/unsubscribe/refresh implemented; RM10/RM11 |
| ReadRoomsChannel | `user_ID_reads` stream `{room_id}` | Per-user delivery; passing read frame |
| UnreadRoomsChannel | `user_ID_unreads` stream raw JSON `{roomId}` | Per-user delivery; independent suite verifies shape/privacy |
| TypingNotificationsChannel | Member room; start/stop user{id,name} broadcasts | Live start matching; existing suite also stop/authorization |
| RoomMessagesChannel | Verified stream, GlobalID room, current membership | Live guarded signature passing; stale typed GID gap RM14 |
| Turbo::StreamsChannel + RoomStreamsAreAuthorized | Signed stream; reject guarded room message suffix | Live bypass rejection passing; arbitrary signed stream restriction RM14 |

Rails normal membership destruction uses `after_destroy_commit` to reset remote connections (`upstream/app/models/membership.rb:7`). The live probe also deleted membership using direct SQL, deliberately bypassing this callback: Rails continued typing delivery while the port fresh lookup emitted disconnect. This is recorded in packets but **not a Rails permission vulnerability claim**. A proper Rails controller/AR revocation and cross-worker replay matrix was not completed in this slice; the parent permission audit should cover it. The independent port suite does verify rejection after revocation and harvested signature replay.

## Complete job/configuration/hook inventory

| Job/hook | Original behavior | Port status |
| --- | --- | --- |
| ApplicationJob | ActiveJob base; application retry/discard examples commented | No equivalent durable job abstraction |
| Bot::WebhookJob | `bot.deliver_webhook(message)` | Combined IntegrationService message queue |
| Room::PushMessageJob | Room::MessagePusher → WebPush::Pool | Same combined queue, sequential push |
| RemoveBannedContentJob | `user.remove_banned_content` asynchronously | Identity performs content deletion + media purge after transaction; synchronous/durability difference, Identity report owns detailed transaction |
| ActiveStorage::BaseJob | ActiveJob base | No equivalent |
| ActiveStorage::AnalyzeJob | Discard missing row; integrity retry10 polynomial | Inline AnalyzeAsync, no durable retry |
| ActiveStorage::PurgeJob | Discard missing row; deadlock retry10 polynomial | Synchronous recursive purge; DB deleted before File.Delete, no retry for filesystem failure |
| ActiveStorage::MirrorJob | Discard missing file; integrity retry10 polynomial | Absent; deployed configured service is local disk, mirror configuration dormant |
| ActiveStorage::TransformJob | Discard missing/unrepresentable; integrity retry10 polynomial | Inline GET transform, no background preprocessed transform job |
| ActiveStorage::PreviewImageJob | Original framework preview job and retry/discard definitions captured | Inline video preview only; no durable job/PDF implementation |
| Message after_create_commit → room.receive | Unread disconnected visible memberships, then enqueue push | Chat/Integration manual side effects; combined queue coupling RM15 |
| Message controller create | Explicit eager attachment processing, broadcast, then bot jobs | Lazy thumb; parent Chat calls media/realtime/integrations |
| Membership after_destroy_commit | Redis Action Cable remote reset/reconnect | Process-local explicit disconnect + heartbeat check |
| Puma startup Membership.disconnect_all | Reset currently connected memberships | Matching host SQL `src/Campfire/Program.cs:38`; inspected, not separately restart-tested |
| Cable adapter | Redis / per-environment prefix | Process-local dictionary |
| Production ActiveJob + Resque initialization | Resque adapter, REDIS_URL; resque-pool worker count | In-memory hosted background worker only |
| WebPush initializer at_exit | Pool shutdown, connection shutdown,1s drain then kill | BackgroundService stopping token; pending queue no persistence |
| Vips initializer | Untrusted loaders + OpenSlide blocked; BMP/ICO/PSD excluded from variants | MIME exclusion partly matched; loader configuration absent |
| ActiveStorage authentication initializer/concern | Direct upload and disk update require session; disk reads public | Matching controls, global CSRF exemption parent-owned |
| Storage paths/config | Active Storage Disk `storage/files`, schema-compatible files,5min URLs | Existing files/schema read and new shards compatible; random key alphabet differs |
| VAPID initializer | ENV values or Rails encrypted credentials fallback | ENV keys only; no credentials decryption fallback |

## Important source/runtime distinctions and unverified areas

- Range: the running Rails image returned200/full text for Range requests following a previously cached GET, with `X-Cache: hit`; JIT/AOT returned206 or416. The captured Rails ProxyController implements byte ranges. This is a front-cache behavior observation, not a claim Rails source lacks ranges. Parent cache/header audit owns the proxy layering; no forced cache-miss range matrix was added.
- PDF: generated real PDF upload succeeded200 but preview_count0 on all three. Source registration vs absent port preview is documented; installed-tool support/first-page byte equality remains unverified.
- Seed JPEG/video/BMP room markup was captured, and PNG thumbnail bytes were fetched; seeded video poster bytes, rotated video/audio metadata, unusual codecs and malformed media were not exhaustively tested.
- Avatar/logo GET and normal signatures passed; weak ETag conditional requests, third-party avatar initials Unicode regex equivalence, all stock image sizes, avatar replacement cleanup under failure and custom-image conditional caching were not exhaustively compared.
- Crash/restart queue durability, multi-instance Cable fanout/revocation, concurrent variant races, slow-consumer queue overflow and sustained malformed WS frames are source-confirmed gaps or risk branches, not executed fault-injection results.
- Existing browser real-service Web Push, TTL/large-payload parity, TLS invalidation, expired vendor subscriptions and all registered webhook MIME responses remain unverified. No real notification/webhook endpoints were contacted.
- Unfurl external DNS rebinding/redirect/charset tests were not run against public hosts; local fake document tests plus original safe-network source were used. Surfguard's complete classification set was not compared exhaustively with the handwritten IP policy.
- Bot message/boost API and bot management routes belong to Chat/Identity audits. This slice verifies integration scheduling/callback implications and local webhook reply transactions; it does not substitute for those route audits.

## Reproduction

With parent's family launcher running (or a fresh isolated launch), from repository root:

```text
python bench/parity/serve.py --family realtime_media --port 4420
python bench/parity/realtime_media/probe.py
python bench/parity/realtime_media/media_probe.py
python bench/parity/realtime_media/capture_framework.py
python bench/parity/realtime_media/inventory.py
python bench/parity/realtime_media/summarize.py
dotnet run --project tests/Parity/RealtimeMedia/RealtimeMedia.Parity.csproj -c Release
dotnet run --project tests/Realtime/Realtime.Tests.csproj -c Release
dotnet run --project tests/Storage/Storage.Tests.csproj -c Release
dotnet run --project tests/Integrations/Integrations.Tests.csproj -c Release
```

On this Windows host `python` was invoked by its configured runtime path `C:\Users\Vaclav\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe`. The audit project compiled in Release with warnings treated as errors and ran successfully, emitting `runtime/local-repro.json`; all three existing suites passed. Probes mutate only disposable family clones; `probe.py` reinstates its test membership before rerun and then removes it to exercise raw-SQL revocation. `media_probe.py` restores that membership before its independent media flow. Start a fresh family for clean-seed reproduction. No benchmark retiming, publication or production modifications were performed.
