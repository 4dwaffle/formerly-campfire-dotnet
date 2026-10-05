"""Independently validate recorded HTTP accounting, captured bytes and database deltas."""
import argparse
import gzip
import hashlib
import json
import pathlib
import re
import statistics


def validate(folder):
    metadata = json.loads((folder / "metadata.json").read_text())
    options = metadata["options"]
    records = [json.loads(p.read_text()) for p in sorted(folder.glob("*-[0-9]*.json"))]
    expected = {(app, rep) for app in options["apps"] for rep in range(1, options["reps"] + 1)}
    assert {(r["app"], r["repetition"]) for r in records} == expected
    assert len(records) == len(expected)
    first_ids = records[0]["captures"]
    measured = warmups = posts = 0
    jobs = []
    for record in records:
        assert not record["protocol_validation_errors"], record["app"]
        for collection in ("http", "warmups"):
            for sample in record[collection]:
                assert sample["errors"] == 0, sample
                allowed = {"200", "201", "204"} if sample["route"] == "post_message" else {"200"}
                assert set(sample["statuses"]) <= allowed, sample
                assert sum(sample["statuses"].values()) == sample["ok"] == sample["latency"]["n"], sample
                if collection == "http":
                    measured += sample["ok"]
                else:
                    warmups += sample["ok"]
        measured_routes = {(s["route"], s["conc"]) for s in record["http"]}
        assert measured_routes == {(r, c) for r in options["routes"] for c in options["concurrencies"]}
        db = record["database_validation"]
        writes = sum(s["ok"] for collection in ("http", "warmups") for s in record[collection]
                     if s["route"] == "post_message")
        assert writes == db["expected_writes"]
        assert db["after"]["messages"] - db["before"]["messages"] == writes
        assert db["after"]["bench_fts_rows"] - db["before"]["bench_fts_rows"] == writes
        posts += writes
        for route, capture in record["captures"].items():
            assert capture["status"] == 200
            basename = f"{record['app']}-{record['repetition']}-{route}"
            body = (folder / (basename + ".html")).read_bytes()
            assert len(body) == capture["bytes"]
            assert hashlib.sha256(body).hexdigest() == capture["sha256"]
            encoded = (folder / (basename + ".encoded")).read_bytes()
            compressed = capture["gzip_response"]
            assert len(encoded) == compressed["bytes"]
            assert hashlib.sha256(encoded).hexdigest() == compressed["sha256"]
            decoded = gzip.decompress(encoded) if compressed["encoding"] == "gzip" else encoded
            assert len(decoded) == compressed["decoded_bytes"]
            ids = sorted(set(v.decode() for v in re.findall(rb'data-message-id="([0-9]+)"', decoded)))
            assert ids == capture["message_ids"]
            if route in ("room", "messages", "search"):
                assert capture["message_ids"] == first_ids[route]["message_ids"]
            # Pagination follows Rails' model validator. Its fresh CSRF bytes
            # do not change that validator; other selected routes use body SHA.
            if route != "messages" and record["app"] in ("dotnet", "aot", "dotnet-baseline"):
                etag = next(v for k, v in compressed["headers"].items() if k.lower() == "etag")
                assert etag == 'W/"' + hashlib.sha256(decoded).hexdigest()[:32] + '"'
        if record.get("durable_jobs_after_http"):
            snapshot = record["durable_jobs_after_http"]
            assert snapshot["failed"] == 0, snapshot
            jobs.append({"app": record["app"], "repetition": record["repetition"], "snapshot": snapshot})
    summary = {}
    for app in options["apps"]:
        summary[app] = {}
        for route in options["routes"]:
            samples = [s for r in records if r["app"] == app for s in r["http"] if s["route"] == route]
            grouped = {}
            for concurrency in sorted({s["conc"] for s in samples}):
                values = [s["rps"] for s in samples if s["conc"] == concurrency]
                grouped[str(concurrency)] = {"median": statistics.median(values), "range": [min(values), max(values)]}
            # Different client counts are different workloads, not repetitions.
            summary[app][route] = next(iter(grouped.values())) if len(grouped) == 1 else {"by_concurrency": grouped}
    result = {"runs": len(records), "measured_responses": measured, "warmup_responses": warmups,
              "successful_persisted_posts": posts, "transport_status_protocol_errors": 0,
              "captured_bytes_hashes_gzip_and_ids_verified": True, "message_fts_growth_match": True,
              "etag_scope": "Complete body SHA for .NET room/sidebar/search/up; pagination uses a Rails model validator, not body SHA.",
              "durable_jobs": jobs, "summary": summary}
    (folder / "validation.json").write_text(json.dumps(result, indent=2) + "\n")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folders", type=pathlib.Path, nargs="+")
    args = parser.parse_args()
    for path in args.folders:
        result = validate(path)
        print(json.dumps({"folder": str(path), **{k: v for k, v in result.items() if k not in ("durable_jobs", "summary")}}))
