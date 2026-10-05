# Implementation ownership and integration contract

Baseline: Rails 6c7f8fa15f8c39478af64a483dcdf6223527ed22; Rust 64f86353021145b63849fb1cd93adeb08f3b8dbb; Go 504428addff333549f1fc88b003c7331779a3c2a.

All implementations live in one ASP.NET Core web project using feature folders. Parent owns Contracts, Program.cs, project/package references, build/container/benchmark scripts and aggregate docs. Ask parent for package changes.

## Agent 1: Identity and persistence

Own Features/Identity/** and Features/Persistence/**, plus tests/Identity/**. Implement IDataStore, IAuthService, IRailsCrypto. Extension methods in namespace Campfire.Features.Identity: AddIdentityFeature(IServiceCollection, IConfiguration), MapIdentityFeature(WebApplication). IDataStore honors CAMPFIRE_STORAGE (default ./storage), db/production.sqlite3, WAL, foreign keys, busy timeout and serialized immediate writes. Build schema for empty installs; existing seeds must work. Include first-run, login/logout, invitations, profile/admin users/bots, account settings, signed IDs/cookies/CSRF and compatibility tests. Current() caches only inside HttpContext; no stale cross-request permissions.

## Agent 2: Chat and presentation

Own Features/Chat/**, Views/**, wwwroot/** and tests/Chat/**. Implement IChatRenderer. Extension methods in namespace Campfire.Features.Chat: AddChatFeature(IServiceCollection, IConfiguration), MapChatFeature(WebApplication). Full room/message/search/boost/involvement/autocomplete routes and templates; preserve original frontend. Use IDataStore (parameterized SQL + Dapper in Write), IAuthService, IRailsCrypto, IMediaService and IRealtimeEvents. After successful message create, call realtime and integration events; writes must update room, unread membership and FTS state. Authorization for every read and write. Identity owns its own HTML forms initially; shared layout can be coordinated later. Coordinate rendering adapter with parent.

## Agent 3: Realtime, storage and integrations

Own Features/Realtime/**, Features/Storage/**, Features/Integrations/** and tests/Realtime/**, tests/Storage/**, tests/Integrations/**. Implement IRealtimeEvents, IMediaService, IIntegrationEvents. Extension methods in namespace Campfire.Features.Realtime: AddRealtimeFeature(IServiceCollection, IConfiguration), MapRealtimeFeature(WebApplication); corresponding AddStorageFeature/MapStorageFeature and AddIntegrationsFeature/MapIntegrationsFeature in matching namespaces. Action Cable protocol and channels with authorization/revocation; bounded send queues; Active Storage uploads/downloads/variants; bot webhooks and Web Push with explicit outbound policies. Don't send fixture hooks or notifications to real external targets in tests/benchmarks. Read source for behavior; document gaps.

Each owner leaves a STATUS.md in its feature directory with verified behavior, test commands, and remaining gaps. Avoid edits outside owned paths. No fake or stub successful endpoints. Parent integrates and assigns follow-up fixes until acceptance criteria are met.
