"""Fresh-install identity/schema audit on newly generated disposable Docker resources.

Reuses the parent harness lifecycle. Deletes only explicit SQLite files in freshly
generated volumes, while their sole application is stopped. Leaves the seed and
the existing identity family untouched. New resources are removed in finally.
"""
import argparse
import datetime
import json
import pathlib
import sys
import uuid
import urllib.parse

from check import Client, HERE

ROOT = HERE.parents[2]
sys.path.insert(0, str(ROOT / "bench"))
from run import Environment, DOTNET_IMAGES, MANIFEST, PARITY_IMAGE, execute


def empty(environment):
    execute("docker", "stop", environment.app)
    execute("docker", "run", "--rm", "--entrypoint", "bash", "-v", environment.volume + ":/data", PARITY_IMAGE,
            "-c", "rm -f /data/db/production.sqlite3 /data/db/production.sqlite3-wal /data/db/production.sqlite3-shm")
    execute("docker", "start", environment.app)
    import time
    import urllib.request
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(environment.base + "/up", timeout=1) as response:
                if response.status == 200:
                    return
        except OSError:
            time.sleep(.2)
    raise RuntimeError("Fresh database instance did not start")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=pathlib.Path, default=HERE / "runtime/results")
    parser.add_argument("--verify-prepare", action="store_true", help="Run pinned Rails db:prepare on a disposable copy of each fresh .NET volume")
    parser.add_argument("--jit-image")
    parser.add_argument("--aot-image")
    args = parser.parse_args()
    if args.jit_image: DOTNET_IMAGES['dotnet'] = args.jit_image
    if args.aot_image: DOTNET_IMAGES['aot'] = args.aot_image
    output = args.output / ("fresh-" + datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ"))
    output.mkdir(parents=True)
    data = {}
    for index, name in enumerate(("rails", "dotnet", "aot")):
        print("Fresh install audit", name, flush=True)
        environment = Environment(argparse.Namespace(server_cpus="0-3", port=4490 + index), output)
        environment.repetition = "fresh"
        with environment.application(name):
            empty(environment)
            instance = {"base": environment.base, "volume": environment.volume}
            client = Client(instance)
            client.get_token("/first_run")
            client.request("first-run-missing-user", "/first_run", "POST", {"irrelevant": "1"})
            missing_responses = client.records.copy()
            missing = client.sql("SELECT (SELECT count(*) FROM accounts) AS accounts,(SELECT count(*) FROM users) AS users,(SELECT count(*) FROM rooms) AS rooms")
            empty(environment)
            client = Client(instance)
            client.get_token("/first_run")
            client.request("first-run-normal", "/first_run", "POST", {"account[name]": "Ignored", "user[name]": "Fresh Admin", "user[email_address]": "fresh@example.invalid", "user[password]": "secret123456"})
            normal = client.sql("SELECT (SELECT name FROM accounts) AS account_name,(SELECT count(*) FROM accounts) AS accounts,(SELECT count(*) FROM users WHERE role=1) AS admins,(SELECT count(*) FROM rooms) AS rooms,(SELECT count(*) FROM memberships) AS memberships,(SELECT count(*) FROM sessions) AS sessions")
            metadata = {"migrations": client.sql("SELECT version FROM schema_migrations ORDER BY version"), "internal": client.sql("SELECT key,value FROM ar_internal_metadata ORDER BY key")}
            # Structural comparisons avoid SQL formatting differences between schema loaders.
            structure = client.sql("SELECT type,name,tbl_name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name")
            tables = {}
            for record in structure:
                if record["type"] == "table":
                    table = record["name"].replace("'", "''")
                    tables[record["name"]] = {"columns": client.sql(f"PRAGMA table_info('{table}')"), "foreign_keys": client.sql(f"PRAGMA foreign_key_list('{table}')")}
            result = {"instance": instance, "missing_params_database": missing, "missing_params_responses": missing_responses, "normal_database": normal, "metadata": metadata, "tables": tables, "sqlite_master": structure, "responses": client.records}
            if name != "rails":
                execute("docker", "stop", environment.app)
                status = execute("docker", "run", "--rm", "--entrypoint", "/rails/bin/rails", "--env-file", ROOT / "reference-rust/parity/.env.reference",
                                 "-v", environment.volume + ":/rails/storage", MANIFEST["rails"]["image"], "db:migrate:status")
                result["rails_migration_status_on_dotnet_database"] = status
                result["rails_pending_migrations"] = sum(line.strip().startswith("down") for line in status.splitlines())
                if args.verify_prepare:
                    copied_volume = "campfire-identity-prepare-" + uuid.uuid4().hex[:12]
                    execute("docker", "volume", "create", copied_volume)
                    try:
                        execute("docker", "run", "--rm", "--entrypoint", "bash", "-v", environment.volume + ":/source:ro", "-v", copied_volume + ":/data", PARITY_IMAGE, "-c", "cp -a /source/. /data/")
                        copied_client = Client({"volume": copied_volume, "base": environment.base})
                        before_prepare = copied_client.sql("SELECT (SELECT count(*) FROM users) AS users,(SELECT count(*) FROM accounts) AS accounts,(SELECT count(*) FROM rooms) AS rooms,(SELECT count(*) FROM memberships) AS memberships,(SELECT count(*) FROM sessions) AS sessions,(SELECT count(*) FROM schema_migrations) AS migrations")
                        result["rails_prepare_output"] = execute("docker", "run", "--rm", "--entrypoint", "/rails/bin/rails", "--env-file", ROOT / "reference-rust/parity/.env.reference", "-v", copied_volume + ":/rails/storage", MANIFEST["rails"]["image"], "db:prepare")
                        after_prepare = copied_client.sql("SELECT (SELECT count(*) FROM users) AS users,(SELECT count(*) FROM accounts) AS accounts,(SELECT count(*) FROM rooms) AS rooms,(SELECT count(*) FROM memberships) AS memberships,(SELECT count(*) FROM sessions) AS sessions,(SELECT count(*) FROM schema_migrations) AS migrations")
                        result["rails_prepare_before"] = before_prepare
                        result["rails_prepare_after"] = after_prepare
                        result["rails_prepare_preserves_records"] = before_prepare == after_prepare
                    finally:
                        execute("docker", "volume", "rm", copied_volume, check=False)
            data[name] = result
            (output / (name + ".json")).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    differences = []
    for name in ("dotnet", "aot"):
        for field in ("missing_params_database", "normal_database", "metadata", "tables"):
            if data[name][field] != data["rails"][field]:
                differences.append({"implementation": name, "field": field})
    summary = {"results": str(output), "differences": differences, "pending_migrations": {name: data[name].get("rails_pending_migrations") for name in ("dotnet", "aot")}}
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=2))
    return 1 if differences else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print("HARNESS FAILURE:", error, file=sys.stderr)
        sys.exit(2)
