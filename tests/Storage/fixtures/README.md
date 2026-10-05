# Pinned storage compatibility inputs

`rails-storage.json` contains only the storage cases needed by this suite, attributed in the file to Campfire `6c7f8fa15f8c39478af64a483dcdf6223527ed22` and the pinned Rust compatibility-vector revision. Its signing key is a public synthetic fixture key, not a deployment secret.

`marcel-pinned.json` was produced by evaluating `Marcel::MimeType.for(StringIO.new(Base64.decode64(bytes)), name: name, declared_type: declared)` for each supplied row in Marcel1.1.0 from the pinned Rails image `ghcr.io/basecamp/once-campfire@sha256:7197fff46e15d0dce69e0a16624df239e1b9bd069534dc28dec67ab7b6771672`. Each row retains the exact binary input, filename, declared type and original Ruby result. Cases intentionally include misleading extensions/declarations, an office ZIP child type, minimal magic bytes and empty input.

The production Marcel catalog is generated from that gem's `EXTENSIONS`, `TYPE_PARENTS`, `MAGIC` and custom definitions. Marcel code is MIT licensed; its MIME data derives from Apache Tika's Apache2.0 catalog. The parent-owned third-party notice records the complete license attribution. Nine golden cases are regression coverage, not an exhaustive codec or catalog conformance claim.
