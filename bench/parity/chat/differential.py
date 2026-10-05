"""Capture chat behavior on disposable Rails/JIT/AOT copies. No production edits.

Only use instances.json from the chat family. --mutate runs real HTTP mutations;
evidence, source fixtures and database snapshots are saved under --output.
"""
import argparse
import datetime
import hashlib
import html
from html.parser import HTMLParser
import http.cookiejar
import json
import pathlib
import re
import subprocess
import urllib.error
import urllib.parse
import urllib.request

HERE = pathlib.Path(__file__).resolve().parent


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Dom(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.elements = []

    def handle_starttag(self, tag, attrs):
        self.elements.append({"tag": tag, **dict(attrs)})


def summary(body):
    text = body.decode("utf-8", "replace")
    dom = Dom()
    dom.feed(text)
    result = {"message_ids": sorted(set(re.findall(r'data-message-id="(\d+)"', text))), "frame_ids": [item.get("id") for item in dom.elements if item["tag"] == "turbo-frame"], "body_classes": [item.get("class") for item in dom.elements if item["tag"] == "body"], "title": re.findall(r"<title>(.*?)</title>", text, re.S), "presentations": re.findall(r'<div id="presentation_message_[^\"]+"[^>]*>([\s\S]*?)</turbo-frame>', text), "editors": [item for item in dom.elements if item["tag"] == "lexxy-editor"], "forms": [item for item in dom.elements if item["tag"] == "form"], "metas": [item for item in dom.elements if item["tag"] == "meta" and item.get("name") != "csrf-token"], "stream_actions": [item for item in dom.elements if item["tag"] == "turbo-stream"], "images": [item for item in dom.elements if item["tag"] == "img"], "lang_attributes": [item for item in dom.elements if "data-language" in item], "has_flash": 'class="flash"' in text}
    try:
        result["json"] = json.loads(text)
    except json.JSONDecodeError:
        pass
    return result


class Client:
    def __init__(self, instance, name, output):
        self.base = instance["base"].rstrip("/")
        self.instance = instance
        self.sql_affinity = ["--cpuset-cpus", instance["client_cpus"]] if instance.get("client_cpus") else []
        self.name = name
        self.output = output / name
        self.output.mkdir(parents=True, exist_ok=True)
        self.records = {}
        self.csrf = ""
        self.message_rooms = {}
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()), NoRedirect())

    def request(self, case, method, path, fields=None, raw=None, headers=None):
        if fields is not None:
            raw = urllib.parse.urlencode(fields, doseq=True).encode()
            headers = {"Content-Type": "application/x-www-form-urlencoded", **(headers or {})}
        req = urllib.request.Request(self.base + path, data=raw, method=method, headers={"Accept-Encoding": "identity", **(headers or {})})
        try:
            response = self.opener.open(req, timeout=45)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            body = response.read()
            response_headers = {key.lower(): value for key, value in response.headers.items() if key.lower() != "set-cookie"}
            record = {"case": case, "method": method, "path": re.sub(r"/\d+-[A-Za-z0-9]+/messages", "/<bot-key>/messages", path), "status": response.status, "headers": response_headers, "bytes": len(body), "sha256": hashlib.sha256(body).hexdigest(), "summary": summary(body)}
        (self.output / (case + ".body")).write_bytes(body)
        self.records[case] = record
        return record, body.decode("utf-8", "replace")

    def login(self, who, labels):
        record, body = self.request(who + "-login-form", "GET", "/session/new")
        dom = Dom()
        dom.feed(body)
        self.csrf = next((item.get("value", "") for item in dom.elements if item.get("name") == "authenticity_token"), "")
        record, body = self.request(who + "-login", "POST", "/session", fields={"email_address": labels["emails." + who], "password": labels["passwords.all"], "authenticity_token": self.csrf})
        if record["status"] not in {302, 303}:
            raise RuntimeError(self.name + " failed login for " + who + ": " + str(record["status"]))
        record, body = self.request(who + "-home", "GET", "/")
        if record["status"] in {302, 303}:
            target = urllib.parse.urlsplit(record["headers"]["location"])
            record, body = self.request(who + "-home-room", "GET", target.path + ("?" + target.query if target.query else ""))
        dom = Dom()
        dom.feed(body)
        self.csrf = next((item.get("content", "") for item in dom.elements if item.get("name") == "csrf-token"), self.csrf)

    def snapshot(self, stage):
        sql = """
        SELECT 'counts' kind,(select count(*) from messages) messages,(select count(*) from message_search_index) fts,(select count(*) from boosts) boosts,(select count(*) from rooms) rooms;
        SELECT m.id,m.room_id,m.creator_id,m.client_message_id,m.created_at,m.updated_at,rt.body,(select body from message_search_index where rowid=m.id) fts_body FROM messages m LEFT JOIN action_text_rich_texts rt ON rt.record_type='Message' and rt.record_id=m.id WHERE m.client_message_id LIKE 'parity-chat-%' ORDER BY m.id;
        SELECT id,room_id,user_id,involvement,unread_at,connected_at,connections,updated_at FROM memberships ORDER BY id;
        SELECT id,name,type,creator_id,updated_at FROM rooms ORDER BY id;
        """
        result = subprocess.run(["docker", "run", "--rm", *self.sql_affinity, "--entrypoint", "sqlite3", "-v", self.instance["volume"] + ":/data", "campfire-parity-dotnet", "-json", "/data/db/production.sqlite3", sql], capture_output=True, text=True, encoding="utf-8", check=True)
        (self.output / (stage + "-database.jsonl")).write_text(result.stdout, encoding="utf-8")

    def load_message_rooms(self):
        result = subprocess.run(["docker", "run", "--rm", *self.sql_affinity, "--entrypoint", "sqlite3", "-v", self.instance["volume"] + ":/data", "campfire-parity-dotnet", "-json", "/data/db/production.sqlite3", "SELECT id,room_id,creator_id,client_message_id FROM messages ORDER BY id"], capture_output=True, text=True, encoding="utf-8", check=True)
        rows = json.loads(result.stdout)
        self.message_rooms = {row["id"]: row["room_id"] for row in rows}
        (self.output / "message-fixture-map.json").write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")

    def read_cases(self, labels):
        room = labels["rooms.watercooler"]
        message = labels["messages.mention"]
        message_room = self.message_rooms[message]
        paths = {
            "welcome": "/", "rooms-index": "/rooms", "room": f"/rooms/{room}", "room-around": f"/rooms/{room}/@{labels['messages.busy_060']}",
            "room-missing": "/rooms/9876543210", "room-invalid-id": "/rooms/not-a-number", "message-page": f"/rooms/{room}/messages", "message-page-before": f"/rooms/{room}/messages?before={labels['messages.busy_060']}", "message-page-after": f"/rooms/{room}/messages?after={labels['messages.busy_060']}", "message-page-invalid-before": f"/rooms/{room}/messages?before=garbage", "message-page-zero-before": f"/rooms/{room}/messages?before=0", "message-page-cross-room": f"/rooms/{room}/messages?before={labels['messages.first']}",
            "message-show": f"/rooms/{room}/messages/{message}", "message-top-level": f"/messages/{message}", "message-edit": f"/rooms/{room}/messages/{message}/edit", "message-json-suffix": f"/rooms/{room}/messages/{message}.json", "page-json-suffix": f"/rooms/{room}/messages.json", "page-stream-suffix": f"/rooms/{room}/messages.turbo_stream", "sidebar": "/users/me/sidebar", "sidebar-other-id": f"/users/{labels['users.kevin']}/sidebar", "autocomplete": "/autocompletable/users?query=da", "autocomplete-json-suffix": "/autocompletable/users.json?query=da", "autocomplete-empty-filter": "/autocompletable/users?filter=&query=da", "autocomplete-room": f"/autocompletable/users?room_id={room}&filter=da", "autocomplete-invalid-room": "/autocompletable/users?room_id=garbage", "open-new": "/rooms/opens/new", "closed-new": "/rooms/closeds/new", "direct-new": "/rooms/directs/new", "open-edit": f"/rooms/opens/{labels['rooms.hq']}/edit", "closed-edit": f"/rooms/closeds/{labels['rooms.designers']}/edit", "direct-edit": f"/rooms/directs/{labels['rooms.david_and_kevin']}/edit", "direct-convert-open": f"/rooms/opens/{labels['rooms.david_and_kevin']}/edit", "settings": f"/rooms/{room}/settings", "involvement": f"/rooms/{room}/involvement", "refresh": f"/rooms/{room}/refresh?since=0", "search-empty": "/searches", "search-coffee": "/searches?q=coffee", "search-punctuation": "/searches?q=coffee%20OR%20pizza", "search-invalid-operator": "/searches?q=OR", "boosts": f"/messages/{message}/boosts", "boost-new": f"/messages/{message}/boosts/new",
        }
        for key in ("message-show", "message-edit", "message-json-suffix"):
            paths[key] = paths[key].replace(f"/rooms/{room}/messages/", f"/rooms/{message_room}/messages/")
        for key, path in paths.items():
            self.request("david-" + key, "GET", path, headers={"Accept": "application/json"} if key.startswith("autocomplete") else None)
        self.request("david-message-json-accept", "GET", paths["message-show"], headers={"Accept": "application/json"})
        for fixture in ["plain", "formatting", "table", "mention", "mention_marshal", "opengraph", "solo_unfurl", "opengraph_trix_tweet", "sound_text", "emoji", "code", "image", "video", "unrenderable"]:
            fixture_id = labels['messages.' + fixture]
            fixture_room = self.message_rooms[fixture_id]
            self.request("fixture-" + fixture, "GET", f"/rooms/{fixture_room}/messages/{fixture_id}")
            self.request("fixture-edit-" + fixture, "GET", f"/rooms/{fixture_room}/messages/{fixture_id}/edit", headers={"Turbo-Frame": "edit_message_test"})
        first = self.records["david-message-page"]
        if "etag" in first["headers"]:
            self.request("david-page-if-none-match", "GET", paths["message-page"], headers={"If-None-Match": first["headers"]["etag"]})
        if "last-modified" in first["headers"]:
            self.request("david-page-if-modified-since", "GET", paths["message-page"], headers={"If-Modified-Since": first["headers"]["last-modified"]})
        self.request("david-page-old-if-modified-since", "GET", paths["message-page"], headers={"If-Modified-Since": "Mon, 05 Oct 2099 00:00:00 GMT"})
        for key in ["message-show", "message-edit", "room", "boost-new", "direct-new", "open-edit", "search-coffee"]:
            self.request("frame-" + key, "GET", paths[key], headers={"Turbo-Frame": "test-frame"})

    def mutations(self, labels):
        room = labels["rooms.watercooler"]
        self.snapshot("before")
        cases = {
            "empty-params": None,
            "formatting": '<p><strong>bold</strong><s>strike</s><u>underline</u><mark>mark</mark></p><pre data-language="ruby"><code>puts 1</code></pre>',
            "unsafe-uri": '<div><a href="javascript:alert(1)" onmouseover="alert(1)">parity-chat-link</a><strong>kept</strong><script>bad</script></div>',
            "same-host-preview": f'<p><action-text-attachment content-type="application/vnd.actiontext.opengraph-embed" href="{self.base}/rooms/{room}" url="{self.base}/rooms/{room}" filename="Parity preview" caption="parity-chat-embed"></action-text-attachment></p>',
            "blank": "",
        }
        for key, content in cases.items():
            fields = {"authenticity_token": self.csrf} if content is None else {"authenticity_token": self.csrf, "message[body]": content, "message[client_message_id]": "parity-chat-" + key}
            self.request("create-" + key, "POST", f"/rooms/{room}/messages", fields=fields, headers={"Accept": "text/vnd.turbo-stream.html"})
        self.request("create-json", "POST", f"/rooms/{room}/messages", raw=json.dumps({"message": {"body": "parity-chat-json", "client_message_id": "parity-chat-json"}}).encode(), headers={"Content-Type": "application/json", "Accept": "application/json", "X-CSRF-Token": self.csrf})
        self.snapshot("after-create")
        # Author/admin semantics and removal are checked through real HTTP and FTS.
        record = self.records["create-formatting"]
        ids = record["summary"]["message_ids"]
        if ids:
            id = ids[0]
            self.request("created-json", "GET", f"/rooms/{room}/messages/{id}", headers={"Accept": "application/json"})
            self.request("update-created", "PATCH", f"/rooms/{room}/messages/{id}", fields={"authenticity_token": self.csrf, "message[body]": "parity-chat-edited"}, headers={"Accept": "application/json"})
            self.request("search-created-update", "GET", "/searches?q=parity%20chat%20edited")
            self.request("delete-created", "DELETE", f"/rooms/{room}/messages/{id}", headers={"X-CSRF-Token": self.csrf, "Accept": "text/vnd.turbo-stream.html"})
        for key, fields in {"closed": {"room[name]": "parity-chat-closed", "user_ids[]": [labels["users.david"], labels["users.jason"]]}, "direct": {"user_ids[]": [labels["users.jz"]]}}.items():
            fields["authenticity_token"] = self.csrf
            self.request("create-room-" + key, "POST", "/rooms/" + ("closeds" if key == "closed" else "directs"), fields=fields)
            if key == "direct":
                self.request("create-room-direct-reuse", "POST", "/rooms/directs", fields=fields)
        self.request("record-search", "POST", "/searches", fields={"authenticity_token": self.csrf, "q": "parity-chat!"})
        self.request("involvement-hide", "PUT", f"/rooms/{room}/involvement", fields={"authenticity_token": self.csrf, "involvement": "invisible"})
        self.request("involvement-hidden-sidebar", "GET", "/users/me/sidebar")
        self.request("involvement-restore", "PUT", f"/rooms/{room}/involvement", fields={"authenticity_token": self.csrf, "involvement": "mentions"})
        self.request("involvement-invalid", "PUT", f"/rooms/{room}/involvement", fields={"authenticity_token": self.csrf, "involvement": "not-valid"})
        message = labels["messages.mention"]
        self.request("create-boost", "POST", f"/messages/{message}/boosts", fields={"authenticity_token": self.csrf, "boost[content]": "parity"})
        self.request("boosts-after-create", "GET", f"/messages/{message}/boosts")
        image_id = labels["messages.image"]
        image_room = self.message_rooms[image_id]
        self.request("clear-attachment-before", "GET", f"/rooms/{image_room}/messages/{image_id}")
        self.request("clear-attachment", "PATCH", f"/rooms/{image_room}/messages/{image_id}", fields={"authenticity_token": self.csrf, "message[attachment]": ""})
        self.request("clear-attachment-after", "GET", f"/rooms/{image_room}/messages/{image_id}")
        self.snapshot("after-human-mutations")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--instances", type=pathlib.Path, default=HERE / "runtime/instances.json")
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--mutate", action="store_true")
    args = parser.parse_args()
    instances = json.loads(args.instances.read_text())
    if instances.get("family") not in ["chat", "remediation-chat"]:
        raise RuntimeError("Use only isolated chat-family instances")
    args.output.mkdir(parents=True, exist_ok=False)
    results = {}
    labels = instances["labels"]
    for name, instance in instances["instances"].items():
        client = Client(instance, name, args.output)
        client.load_message_rooms()
        client.request("anonymous-room", "GET", "/rooms/" + str(labels["rooms.watercooler"]))
        client.login("david", labels)
        client.read_cases(labels)
        if args.mutate:
            client.mutations(labels)
        for user in ("jz", "kevin", "loner"):
            viewer = Client(instance, name, args.output)
            viewer.login(user, labels)
            room = labels["rooms.watercooler"]
            message = labels["messages.mention"]
            for key, path in {"room": f"/rooms/{room}", "page": f"/rooms/{room}/messages", "autocomplete-room": f"/autocompletable/users?room_id={room}", "show-message": f"/rooms/{room}/messages/{message}", "involvement": f"/rooms/{room}/involvement", "boost-new": f"/messages/{message}/boosts/new", "open-edit": f"/rooms/opens/{labels['rooms.hq']}/edit", "search": "/searches?q=coffee"}.items():
                viewer.request(user + "-" + key, "GET", path)
            if args.mutate:
                viewer.request(user + "-update-foreign-message", "PATCH", f"/rooms/{labels['rooms.designers']}/messages/{labels['messages.first']}", fields={"authenticity_token": viewer.csrf, "message[body]": "forbidden parity"})
                viewer.request(user + "-post-inaccessible-room", "POST", f"/rooms/{room}/messages", fields={"authenticity_token": viewer.csrf, "message[body]": "forbidden parity"})
                viewer.request(user + "-delete-shared-room-as-direct", "DELETE", f"/rooms/directs/{labels['rooms.hq']}", headers={"X-CSRF-Token": viewer.csrf})
            client.records.update(viewer.records)
        bot = Client(instance, name, args.output)
        path = f"/rooms/{labels['rooms.watercooler']}/{labels['bot_keys.bender']}/messages"
        bot.request("bot-index", "GET", path)
        bot.request("bot-index-suffix", "GET", path + ".json")
        bot.request("bot-invalid-key", "GET", f"/rooms/{labels['rooms.watercooler']}/invalid-bot-key/messages")
        bot.request("bot-inaccessible-room", "GET", f"/rooms/{labels['rooms.designers']}/{labels['bot_keys.bender']}/messages")
        if args.mutate:
            for key, content_type, raw in [("text", "text/plain", b"parity-chat-bot-plain"), ("curl-default", "application/x-www-form-urlencoded", b"parity-chat-bot-curl"), ("blank", "text/plain", b"   ")]:
                bot.request("bot-create-" + key, "POST", path, raw=raw, headers={"Content-Type": content_type})
            bot.request("bot-boost-curl-default", "POST", path + f"/{labels['messages.fourth']}/boosts", raw=b"nice", headers={"Content-Type": "application/x-www-form-urlencoded"})
            client.snapshot("final")
        client.records.update(bot.records)
        results[name] = client.records
        (args.output / name / "responses.json").write_text(json.dumps(client.records, indent=2) + "\n", encoding="utf-8")
    differences = []
    baseline = results["rails"]
    for name, records in results.items():
        if name == "rails":
            continue
        for case, record in records.items():
            expected = baseline.get(case)
            if expected is None:
                continue
            different = {key: {"rails": expected[key], name: record[key]} for key in ("status",) if record[key] != expected[key]}
            for key in ("content-type", "last-modified", "x-total-count", "location"):
                left = expected["headers"].get(key)
                right = record["headers"].get(key)
                if key == "location":
                    left = left.replace(instances["instances"]["rails"]["base"], "<origin>") if left else left
                    right = right.replace(instances["instances"][name]["base"], "<origin>") if right else right
                if left != right:
                    different[key] = {"rails": left, name: right}
            # IDs added by mutation can differ; this is an observation, not parity certification.
            for key in ("frame_ids", "body_classes", "message_ids"):
                if expected["summary"][key] != record["summary"][key]:
                    different[key] = {"rails": expected["summary"][key], name: record["summary"][key]}
            if different:
                differences.append({"case": case, "app": name, "differences": different})
    result = {"date_utc": datetime.datetime.now(datetime.UTC).isoformat(), "instances": {name: {key: value for key, value in instance.items() if key != "labels"} for name, instance in instances["instances"].items()}, "mutations": args.mutate, "cases_per_app": {name: len(records) for name, records in results.items()}, "observed_differences": differences, "limitations": ["Exact HTML/JSON semantics require reviewing saved bodies", "No browser interaction or websocket delivery checks", "No fault injection or transactional concurrent revocation", "No production modifications"]}
    (args.output / "comparison.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(args.output), "cases_per_app": result["cases_per_app"], "observed_difference_count": len(differences)}))


if __name__ == "__main__":
    main()
