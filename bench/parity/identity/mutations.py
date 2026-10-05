"""Final identity malformed-input, rollback and restored-session differentials.

Only a parent-provisioned disposable identity family is accepted. No build/load.
"""
import argparse
import datetime
import json
import pathlib
import uuid
from check import Client, HERE


def run(instance, labels, suffix):
    client = Client(instance)
    client.login(labels, "david")
    owner = labels["users.david"]
    snapshots = {}
    before = client.sql(f"SELECT name,email_address,bio,updated_at FROM users WHERE id={owner}")
    client.request("duplicate-profile-rollback", "/users/me/profile", "PATCH", json_body={"user": {"name": "Should rollback", "bio": "Should rollback", "email_address": labels["emails.jason"]}})
    snapshots["duplicate-profile-rollback"] = before == client.sql(f"SELECT name,email_address,bio,updated_at FROM users WHERE id={owner}")
    client.request("bad-avatar-profile-rollback", "/users/me/profile", "PATCH", json_body={"user": {"name": "Should rollback", "avatar": "invalid-signed-blob"}})
    snapshots["bad-avatar-profile-rollback"] = before == client.sql(f"SELECT name,email_address,bio,updated_at FROM users WHERE id={owner}")
    for index, root in enumerate((True, False, "nonblank", [1], None, "  ", {}, [])):
        client.request("profile-root-" + str(index), "/users/me/profile", "PATCH", json_body={"user": root})
    client.request("profile-boolean-string-cast", "/users/me/profile", "PATCH", json_body={"user": {"name": True, "bio": False}})
    snapshots["profile-boolean-string-cast"] = client.sql(f"SELECT name,bio FROM users WHERE id={owner}")
    client.sql(f"UPDATE users SET updated_at='2000-01-01 00:00:00' WHERE id={owner}")
    client.request("profile-noop-timestamp", "/users/me/profile", "PATCH", json_body={"user": {"name": "t", "bio": "f"}})
    snapshots["profile-noop-timestamp"] = client.sql(f"SELECT updated_at FROM users WHERE id={owner}")
    client.request("profile-numeric-password", "/users/me/profile", "PATCH", json_body={"user": {"password": 23}})
    client.request("account-null-name", "/account", "PATCH", json_body={"account": {"name": None}})
    client.sql("UPDATE accounts SET custom_styles='preserve'")
    client.request("styles-omitted-permitted", "/account/custom_styles", "PATCH", json_body={"account": {"unpermitted": True}})
    snapshots["styles-omitted-permitted"] = client.sql("SELECT custom_styles FROM accounts")
    client.request("styles-null", "/account/custom_styles", "PATCH", json_body={"account": {"custom_styles": None}})
    snapshots["styles-null"] = client.sql("SELECT custom_styles FROM accounts")
    client.request("json-body-csrf", "/account", "PATCH", json_body={"authenticity_token": client.token, "account": {"name": "JSON CSRF"}}, csrf=False)
    snapshots["json-body-csrf"] = client.sql("SELECT name FROM accounts")
    bot_name = "Credentials " + suffix
    bot = client.sql(f"INSERT INTO users(name,email_address,password_digest,role,status,bot_token,created_at,updated_at) SELECT '{bot_name}','{suffix}@example.invalid',password_digest,2,0,'alphanumeric','2000-01-01','2000-01-01' FROM users WHERE id={owner} RETURNING id")[0]["id"]
    anonymous = Client(instance)
    anonymous.get_token()
    anonymous.request("login-bot-key-is-ignored", "/session/new?bot_key=" + str(bot) + "-alphanumeric")
    anonymous.request("login-bot-password", "/session", "POST", {"email_address": suffix + "@example.invalid", "password": labels["passwords.all"]})
    anonymous.request("credentialed-bot-profile", "/users/me/profile")
    anonymous.sql(f"UPDATE users SET status=1 WHERE id={bot}")
    anonymous.request("inactive-imported-session-profile", "/users/me/profile")
    anonymous.get_token("/users/me/profile")
    anonymous.request("logout-reset-cookie", "/session", "DELETE")
    return {"responses": client.records + anonymous.records, "snapshots": snapshots}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--instances", type=pathlib.Path, default=HERE / "runtime/instances.json")
    parser.add_argument("--output", type=pathlib.Path, default=HERE / "runtime/results")
    args = parser.parse_args()
    family = json.loads(args.instances.read_text(encoding="utf-8-sig"))
    if family.get("family") != "identity" or set(family["instances"]) != {"rails", "dotnet", "aot"}:
        raise RuntimeError("Refusing non-identity family")
    suffix = "mutations-" + uuid.uuid4().hex[:8]
    output = args.output / suffix
    output.mkdir(parents=True)
    results, differences = {}, []
    for name, instance in family["instances"].items():
        print("Mutation parity", name, flush=True)
        results[name] = run(instance, family["labels"], suffix)
        (output / (name + ".json")).write_text(json.dumps(results[name], indent=2) + "\n")
    baseline = {r["case"]: r for r in results["rails"]["responses"] if r["case"] != "csrf-page"}
    for name in ("dotnet", "aot"):
        for row in results[name]["responses"]:
            if row["case"] == "csrf-page":
                continue
            expected = baseline[row["case"]]
            for field in ("status", "location", "content_type"):
                if row[field] != expected[field]:
                    differences.append({"implementation": name, "case": row["case"], "field": field, "rails": expected[field], "actual": row[field]})
        for case, value in results[name]["snapshots"].items():
            if value != results["rails"]["snapshots"][case]:
                differences.append({"implementation": name, "case": case, "field": "database", "rails": results["rails"]["snapshots"][case], "actual": value})
    summary = {"utc": datetime.datetime.now(datetime.UTC).isoformat(), "cases": len(baseline), "differences": differences, "results": str(output)}
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    return 1 if differences else 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print("HARNESS FAILURE:", error)
        raise SystemExit(2)
