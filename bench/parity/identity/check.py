"""Differential identity audit; mutates only the disposable family in instances.json.

Use the bundled Python (standard library only). No server build or source changes.
Raw responses and database snapshots are retained under runtime/results/.
Exit 1 means observed parity differences; exit 2 means a harness/runtime failure.
"""
import argparse
import datetime
import hashlib
import http.cookiejar
import json
import pathlib
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

HERE = pathlib.Path(__file__).resolve().parent
UA = "Mozilla/5.0 Chrome/131.0.0.0 Safari/537.36"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Client:
    def __init__(self, instance):
        self.instance = instance
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(NoRedirect(), urllib.request.HTTPCookieProcessor(self.jar))
        self.records = []
        self.token = None

    def request(self, case, path, method="GET", fields=None, headers=None, json_body=None, csrf=True):
        headers = {"User-Agent": UA, "Accept": "text/html", **(headers or {})}
        data = None
        if method not in ("GET", "HEAD"):
            if json_body is not None:
                data = json.dumps(json_body).encode()
                headers["Content-Type"] = "application/json"
                if csrf and self.token:
                    headers.setdefault("X-CSRF-Token", self.token)
            else:
                fields = dict(fields or {})
                if csrf and self.token:
                    fields.setdefault("authenticity_token", self.token)
                data = urllib.parse.urlencode(fields).encode()
                headers["Content-Type"] = "application/x-www-form-urlencoded"
        req = urllib.request.Request(self.instance["base"] + path, data=data, headers=headers, method=method)
        try:
            response = self.opener.open(req, timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        body = response.read()
        record = {"case": case, "method": method, "path": path, "status": response.code,
                  "headers": list(response.headers.items()), "body": body.decode("utf-8", errors="replace"),
                  "body_sha256": hashlib.sha256(body).hexdigest()}
        location = response.headers.get("Location")
        record["location"] = None if not location else urllib.parse.urlparse(location)._replace(scheme="", netloc="").geturl()
        record["content_type"] = response.headers.get("Content-Type", "").split(";")[0]
        self.records.append(record)
        return record

    def get_token(self, path="/session/new"):
        record = self.request("csrf-page", path)
        match = re.search(r'<meta\s+name="csrf-token"\s+content="([^"]+)"', record["body"])
        if not match:
            match = re.search(r'name="authenticity_token"[^>]*value="([^"]+)"', record["body"])
        if not match:
            raise RuntimeError(f"No CSRF token in {self.instance['base']}{path} ({record['status']})")
        self.token = match[1]
        return self.token

    def login(self, labels, who):
        self.get_token()
        result = self.request("login-" + who, "/session", "POST", {"email_address": labels["emails." + who], "password": labels["passwords.all"]})
        if result["status"] != 302:
            raise RuntimeError(f"Fixture login failed: {result['status']} ({who}, {self.instance['base']})")
        self.get_token("/users/me/profile")

    def sql(self, query):
        command = ["docker", "run", "--rm", "--entrypoint", "sqlite3", "-v", self.instance["volume"] + ":/data",
                   "campfire-parity-dotnet", "-json", "/data/db/production.sqlite3", query]
        result = subprocess.run(command, check=True, capture_output=True, text=True, encoding="utf-8")
        return json.loads(result.stdout) if result.stdout.strip() else []


def run(instance, labels, suffix):
    anonymous, admin, member = Client(instance), Client(instance), Client(instance)
    snapshots = {}
    for case, path, method in [
        ("login-page", "/session/new", "GET"), ("login-head", "/session/new", "HEAD"),
        ("login-html-format", "/session/new.html", "GET"),
        ("session-get-extra-route", "/session", "GET"),
        ("invalid-join", "/join/invalid-code", "GET"),
        ("auth-return-to", "/account/edit?parity=1", "GET")]:
        anonymous.request(case, path, method)
    admin.login(labels, "david")
    member.login(labels, "jason")
    for case, path in [("account-edit", "/account/edit"), ("account-get-extra-route", "/account"),
                       ("custom-styles-extra-route", "/account/custom_styles"),
                       ("account-users-html", "/account/users"), ("account-users-stream", "/account/users.turbo_stream"),
                       ("profile-page", "/users/me/profile"), ("bot-show-extra-route", f"/account/bots/{labels['users.bender']}")]:
        admin.request(case, path)
    member.request("member-account-update-denied", "/account", "PATCH", {"account[name]": "Denied"})
    member.request("member-bots-denied", "/account/bots")
    admin.request("account-missing-nested-params", "/account", "PATCH", {"irrelevant": "1"})
    member.request("profile-missing-nested-params", "/users/me/profile", "PATCH", {"irrelevant": "1"})
    member.request("profile-duplicate-email", "/users/me/profile", "PATCH", {"user[email_address]": labels["emails.david"]})
    member.request("profile-password-over-72-bytes", "/users/me/profile", "PATCH", {"user[password]": "p" * 73})
    member.request("profile-json-body", "/users/me/profile", "PATCH", json_body={"user": {"bio": "JSON parity " + suffix}})
    snapshots["profile-json-body"] = member.sql(f"SELECT bio FROM users WHERE id={labels['users.jason']}")
    admin.request("account-settings-nonboolean", "/account", "PATCH", {"account[settings][restrict_room_creation_to_administrators]": "banana"})
    snapshots["account-settings-nonboolean"] = admin.sql("SELECT settings FROM accounts")
    # Rails checks any supplied token, not just the first source. Do not log the supplied token twice.
    admin.request("csrf-invalid-form-valid-header", "/account", "PATCH", {"authenticity_token": "invalid", "account[name]": "CSRF header accepted"}, headers={"X-CSRF-Token": admin.token})
    admin.request("csrf-header-only", "/account", "PATCH", {"account[name]": "CSRF header only"}, headers={"X-CSRF-Token": admin.token}, csrf=False)
    admin.request("csrf-cross-site-header", "/account", "PATCH", {"account[name]": "Cross site"}, headers={"Sec-Fetch-Site": "cross-site"})
    admin.request("csrf-missing", "/account", "PATCH", {"account[name]": "Missing"}, csrf=False)
    admin.request("csrf-cross-origin", "/account", "PATCH", {"account[name]": "Cross origin"}, headers={"Origin": "https://elsewhere.invalid"})
    bot_name = "ParityBot-" + suffix
    admin.request("bot-create-blank-webhook", "/account/bots", "POST", {"user[name]": bot_name, "user[webhook_url]": ""})
    bot = admin.sql("SELECT id FROM users WHERE name='" + bot_name + "'")[0]["id"]
    snapshots["bot-create-blank-webhook"] = admin.sql(f"SELECT count(*) AS webhooks FROM webhooks WHERE user_id={bot}")
    admin.request("bot-set-webhook", f"/account/bots/{bot}", "PATCH", {"user[name]": bot_name, "user[webhook_url]": "http://127.0.0.1:9/local-only"})
    before = admin.sql(f"SELECT id,created_at FROM webhooks WHERE user_id={bot}")
    admin.request("bot-update-webhook", f"/account/bots/{bot}", "PATCH", {"user[name]": bot_name, "user[webhook_url]": "http://127.0.0.1:9/local-updated"})
    after = admin.sql(f"SELECT id,created_at FROM webhooks WHERE user_id={bot}")
    snapshots["bot-update-webhook"] = {"id_preserved": before[0]["id"] == after[0]["id"], "created_at_preserved": before[0]["created_at"] == after[0]["created_at"]}
    admin.request("bot-whitespace-webhook", f"/account/bots/{bot}", "PATCH", {"user[webhook_url]": "   "})
    snapshots["bot-whitespace-webhook"] = admin.sql(f"SELECT count(*) AS webhooks FROM webhooks WHERE user_id={bot}")
    # Clean local stub hook before any content mutation, preventing an external callback.
    admin.sql(f"DELETE FROM webhooks WHERE user_id={bot}")
    admin.request("bot-delete", f"/account/bots/{bot}", "DELETE")
    snapshots["bot-delete"] = admin.sql(f"SELECT status,(SELECT count(*) FROM memberships WHERE user_id={bot} AND room_id IN (SELECT id FROM rooms WHERE type<>'Rooms::Direct')) AS shared_memberships FROM users WHERE id={bot}")
    join = Client(instance)
    join.get_token("/join/" + labels["join_codes.signal"])
    join.request("join-create", "/join/" + labels["join_codes.signal"], "POST", {"user[name]": "Parity User " + suffix, "user[email_address]": suffix + "@example.invalid", "user[password]": "secret123456"})
    new_user = join.sql("SELECT id FROM users WHERE email_address='" + suffix + "@example.invalid'")[0]["id"]
    snapshots["join-create"] = join.sql(f"SELECT status,role,(SELECT count(*) FROM memberships WHERE user_id={new_user}) AS memberships,(SELECT count(*) FROM rooms WHERE type='Rooms::Open') AS open_rooms,(SELECT count(*) FROM sessions WHERE user_id={new_user}) AS sessions FROM users WHERE id={new_user}")
    admin.request("role-missing-nested-params", f"/account/users/{new_user}", "PATCH", {"irrelevant": "1"})
    admin.request("role-invalid-falls-back-to-member", f"/account/users/{new_user}", "PATCH", {"user[role]": "bot"})
    snapshots["role-invalid-falls-back-to-member"] = admin.sql(f"SELECT role FROM users WHERE id={new_user}")
    admin.request("deactivate-user", f"/account/users/{new_user}", "DELETE")
    snapshots["deactivate-user"] = admin.sql(f"SELECT status,(SELECT count(*) FROM sessions WHERE user_id={new_user}) AS sessions,(SELECT count(*) FROM memberships WHERE user_id={new_user}) AS memberships,email_address LIKE '%-deactivated-%@%' AS email_rewritten FROM users WHERE id={new_user}")
    admin.request("logout", "/session", "DELETE")
    admin.request("after-logout-protected", "/account/edit")
    records = anonymous.records + admin.records + member.records + join.records
    return {"instance": instance, "responses": records, "snapshots": snapshots}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--instances", type=pathlib.Path, default=HERE / "runtime/instances.json")
    parser.add_argument("--output", type=pathlib.Path, default=HERE / "runtime/results")
    args = parser.parse_args()
    family = json.loads(args.instances.read_text(encoding="utf-8-sig"))
    if family.get("family") != "identity" or set(family["instances"]) != {"rails", "dotnet", "aot"}:
        raise RuntimeError("Refusing to mutate a family other than the isolated identity Rails/JIT/AOT family")
    suffix = "parity-" + uuid.uuid4().hex[:10]
    output = args.output / suffix
    output.mkdir(parents=True)
    data = {}
    for name, instance in family["instances"].items():
        print("Auditing", name, flush=True)
        data[name] = run(instance, family["labels"], suffix)
        (output / (name + ".json")).write_text(json.dumps(data[name], indent=2) + "\n", encoding="utf-8")
    differences = []
    baseline = {r["case"]: r for r in data["rails"]["responses"] if r["case"] != "csrf-page"}
    for name in ("dotnet", "aot"):
        for row in data[name]["responses"]:
            if row["case"] == "csrf-page":
                continue
            expected = baseline[row["case"]]
            for field in ("status", "location", "content_type"):
                if row[field] != expected[field]:
                    differences.append({"implementation": name, "case": row["case"], "field": field, "rails": expected[field], "actual": row[field]})
        for case, actual in data[name]["snapshots"].items():
            expected = data["rails"]["snapshots"][case]
            if actual != expected:
                differences.append({"implementation": name, "case": case, "field": "database", "rails": expected, "actual": actual})
    summary = {"utc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "results": str(output), "differences": differences,
               "cases": len(baseline), "scope": "Response status/normalized redirect/content type and selected database side effects; not a bytewise HTML parity test."}
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=2))
    return 1 if differences else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print("HARNESS FAILURE:", error, file=sys.stderr)
        sys.exit(2)
