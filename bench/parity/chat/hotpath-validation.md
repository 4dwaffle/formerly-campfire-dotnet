# Latest hotpath Chat validation

**Stored rich-text canonicalization remains an unresolved parity gap.** The focused POST gate passes **98/110**, with **12 failures**: each difference occurs for both human and bot creation in each .NET executable. [Exact inputs, persisted SQL values and assertions](post-assertions-hotpath.json) retain the Rails observations. Status, FTS plaintext and persisted-rendering checks pass; exact stored-body checks expose:

| Input | Pinned Rails stored body | JIT/AOT stored body |
| --- | --- | --- |
| NBSP + `plain` + NBSP | `&nbsp;plain&nbsp;` | literal U+00A0 on both sides |
| `a\r\nb\rc` | `a\nb\nc` | `a\r\nb\rc` |
| `one &amp; two &#x2615;` | `one &amp; two ☕` | original entity spelling |

This is an existing storage-boundary difference, not a plaintext-shortcut output failure: both executables index the same plaintext as Rails for all five vectors. `ChatStore.StoredBody` strips the fragment, whereas Rails Action Text parses/serializes it before persistence. Equivalent-HTML dirty tracking may also differ; that consequence was not probed here. Parent explicitly retained frozen production source and requested documentation rather than a fix in this phase. No universal parity claim is supported.

## Frozen executable scope and totals

[Manifest](instances-hotpath.json) identifies runtime hash `e2d3a6b2373625d956b72648f045835c4c112e9b0c31295a0a1ccaca3d40bf46`, JIT image `sha256:e546b2f839cf3c86a515820159a6ada192005773ad86db09c91bcf9f308f623a`, AOT image `sha256:dd2f3284584cc4c0f97a04a0065d4fba2cc423d38b576e5a5ea14dace2fce13a` and pinned Rails image `sha256:7197fff46e15d0dce69e0a16624df239e1b9bd069534dc28dec67ab7b6771672`. Fragment gzip plans were explicitly enabled. No production code, image, reference checkout or benchmark harness was changed by this validation.

[Verification](verification-hotpath.json) derives **316 checks: 304 passing, 12 failing**, and **546 complete response records** from actual saved captures. Seven additional consumed bodies from an aborted initial probe lack complete status/header records; their byte counts and hashes are retained separately. Thus **553 bodies** were consumed, including retries and incomplete evidence. The final selected gates consume 384 complete response records; the other 162 complete records belong to preserved initial checker/precondition attempts. No HTTP transport exception occurred. The initial probe aborted on its own missing-header assumption, as described below.

| Final gate | Pass / total | Final complete HTTP records |
| --- | ---: | ---: |
| Original cache/no-op/genuine edit | 47 / 47 | 45 |
| Source-traced behavior/converter vectors | 17 / 17 | 105 |
| Frame/model validators plus page inputs | 22 / 22 | 42 |
| Extended gzip, pagination/search, validators and session | 120 / 120 | 120 |
| Human/bot Unicode, whitespace, CR and entity POST | 98 / 110 | 72 |

The broader 223-check suite belongs to the previous 7702 snapshot and was not repeated on these images. The focused controls observe successful gzip decompression, consumed byte lengths, warm/identity message IDs, zero-quality negotiation, fresh metadata/composer tokens, cross-user token rejection without a boost, accepted own-token persistence, and denial when replaying the original cookie after logout. Message-page validators return 304; dynamic application pages return fresh 200 bodies. This HTTP evidence does not expose the internal immutable-fragment cache flag. Its provenance remains local test evidence.

## Preserved checker/precondition mistakes

- [Initial compressed gate](compressed-initial-assertions-hotpath.json) passes 111/120. Nine failures came from a blanket 304 expectation for room/sidebar/search. Rails, JIT and AOT each returned 200 on those fresh-CSRF application pages. The corrected probe checks that observed original behavior while retaining model-based 304 checks. [Final gate](compressed-assertions-hotpath.json) passes 120/120; the original raw wire/body captures remain under `runtime/hotpath/compressed`.
- [Initial frame gate](frame-initial-assertions-hotpath.json) passes 16/22. The preceding POST probe had left bot messages with generated client UUIDs, so the default-page model versions correctly differed by real creation time across applications. The input cleanup was extended to remove exact owned IDs from the saved POST/source captures. The recorded cleanup SQL does not rewrite seeded IDs or timestamps. Repeating against the restored seed message sets yields [22/22](frame-assertions-hotpath.json). Both frame attempts and page inputs are retained.
- The initial POST probe reused a logged-in human cookie for its bot request, which Rails handles as the cookie user and rejects without CSRF (422). The probe aborted when it expected a bot-created Location header. Its seven consumed `.body` files remain under `runtime/hotpath/post`; complete status/header evidence was not written. The completed probe uses a separate cookie-free bot client and preserves its genuine 12 storage failures.

## Reproduction and cleanup

```powershell
python bench/parity/serve.py --family chat --port 4450 --server-cpus 8-11 --client-cpus 12-15 --fragment-gzip 1 --jit-image campfire-dotnet:hotpath-jit --aot-image campfire-dotnet:hotpath-aot --output bench/parity/chat/runtime/hotpath/instances
python bench/parity/chat/cache_dependencies.py --instances bench/parity/chat/instances-hotpath.json --output bench/parity/chat/runtime/hotpath/cache
python bench/parity/chat/cache_assertions.py --capture bench/parity/chat/cache-policy-hotpath.json --output bench/parity/chat/cache-assertions-hotpath.json
python bench/parity/chat/post_text.py --instances bench/parity/chat/instances-hotpath.json --output bench/parity/chat/runtime/hotpath/post-02
python bench/parity/chat/source_branches.py --instances bench/parity/chat/instances-hotpath.json --output bench/parity/chat/runtime/hotpath/source
python bench/parity/chat/frame_inputs.py --instances bench/parity/chat/instances-hotpath.json --output bench/parity/chat/runtime/hotpath/frame-inputs-02 --post-capture bench/parity/chat/post-assertions-hotpath.json --source-capture bench/parity/chat/runtime/hotpath/source
python bench/parity/chat/frame_validators.py --instances bench/parity/chat/instances-hotpath.json --main bench/parity/chat/runtime/hotpath/frame-inputs-02 --output bench/parity/chat/runtime/hotpath/frame-02
python bench/parity/chat/compressed_pages.py --extended --instances bench/parity/chat/instances-hotpath.json --output bench/parity/chat/runtime/hotpath/compressed-02
python bench/parity/chat/save_verification.py --suffix hotpath --runtime bench/parity/chat/runtime/hotpath --gates cache-assertions,source-assertions,frame-assertions,compressed-assertions,post-assertions --historical-gates compressed-initial-assertions,frame-initial-assertions
```

Output directories must be fresh. The launcher manifest was copied/enriched with the declared runtime hash and SQL-helper `client_cpus=12-15`. Portable assertion files were copied from their runtime results; raw `.wire`, `.html`, `.body`, response JSON, headers and fixture-change SQL remain under the ignored runtime directory. The helper rejects non-Chat families and removes only explicitly owned created messages before frame comparisons.

Servers used CPUs 8–11 and Docker SQL helpers 12–15. Windows HTTP clients and shared Docker/I/O were not isolated. The benchmark had finished before startup. Tests used local fixtures; no external link fetch or outbound contact was introduced. [Cleanup](cleanup-hotpath.json) records zero remaining owned containers, volumes and networks. No builds, benchmarks, commits, pushes or publications were performed in this validation phase.
