"""Focused chat mutation/fixture probes on the disposable chat family only."""
import argparse
import datetime
import html
import json
import pathlib
import re
import subprocess
from differential import Client, HERE


def sql(client, statement):
    affinity = ["--cpuset-cpus", client.instance["client_cpus"]] if client.instance.get("client_cpus") else []
    result = subprocess.run(["docker", "run", "--rm", *affinity, "--entrypoint", "sqlite3", "-v", client.instance["volume"] + ":/data", "campfire-parity-dotnet", "-json", "/data/db/production.sqlite3", statement], capture_output=True, text=True, encoding="utf-8", check=True)
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--instances", type=pathlib.Path, default=HERE / "runtime/instances.json")
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    setup = json.loads(args.instances.read_text())
    if setup.get("family") not in ["chat", "remediation-chat"]:
        raise RuntimeError("Use only the isolated chat family")
    args.output.mkdir(parents=True, exist_ok=False)
    labels = setup["labels"]
    evidence = {}
    for app, instance in setup["instances"].items():
        client = Client(instance, app, args.output)
        client.login("david", labels)
        room = labels["rooms.watercooler"]
        fixture_changes = []
        # Direct SQL is fixture setup, not an implementation of tested mutations.
        statement = f"UPDATE memberships SET unread_at=NULL,connected_at=NULL,connections=0,involvement='mentions' WHERE room_id={room}; UPDATE memberships SET connected_at=datetime('now'),connections=1 WHERE room_id={room} AND user_id={labels['users.jason']};"
        fixture_changes.append(statement)
        sql(client, statement)
        cases = {
            "solo-legacy": '<div>https://basecamp.com/<action-text-attachment content-type="application/vnd.actiontext.opengraph-embed" href="https://basecamp.com/" url="https://basecamp.com/logo.png" filename="Basecamp" caption="Project management"></action-text-attachment></div>',
            "ip-preview": '<p><action-text-attachment content-type="application/vnd.actiontext.opengraph-embed" href="http://127.0.0.1/page" url="http://127.0.0.1/image.png" filename="IP title" caption="IP description"></action-text-attachment></p>',
            "handwritten-content": '<p><action-text-attachment content-type="application/vnd.actiontext.opengraph-embed" content="' + html.escape('<actiontext-opengraph-embed data-controller="parity-pwn" data-action="click->parity-pwn#run"><div class="og-embed"><div class="og-embed__title"><a href="https://example.com/">Title</a></div><div class="og-embed__description">Description</div><div class="og-embed__image"><img src="https://example.com/image.png" onerror="parityPwn()"></div></div></actiontext-opengraph-embed>', quote=True) + '"></action-text-attachment></p>',
        }
        for case, body in cases.items():
            record, _ = client.request(case + "-create", "POST", f"/rooms/{room}/messages", fields={"authenticity_token": client.csrf, "message[body]": body, "message[client_message_id]": "parity-chat-" + case}, headers={"Accept": "text/vnd.turbo-stream.html"})
            ids = record["summary"]["message_ids"]
            if ids:
                client.request(case + "-edit", "GET", f"/rooms/{room}/messages/{ids[0]}/edit", headers={"Turbo-Frame": "edit_message_parity"})
        client.snapshot("connected-unread")
        # Deletion failure demonstrates whether room rename and membership revision
        # share a transaction. The abort trigger exists only during this one request.
        record, _ = client.request("fault-room-create", "POST", "/rooms/closeds", fields={"authenticity_token": client.csrf, "room[name]": "parity-chat-fault-before", "user_ids[]": [labels["users.david"], labels["users.jason"]]})
        fault_room = int(record["headers"]["location"].rstrip("/").split("/")[-1])
        statement = f"CREATE TRIGGER parity_chat_membership_fault BEFORE DELETE ON memberships WHEN OLD.room_id={fault_room} BEGIN SELECT RAISE(ABORT,'parity-chat-membership-fault'); END;"
        fixture_changes.append(statement)
        sql(client, statement)
        try:
            client.request("fault-room-update", "PATCH", f"/rooms/closeds/{fault_room}", fields={"authenticity_token": client.csrf, "room[name]": "parity-chat-fault-after", "user_ids[]": [labels["users.david"]]})
        finally:
            sql(client, "DROP TRIGGER parity_chat_membership_fault;")
            fixture_changes.append("DROP TRIGGER parity_chat_membership_fault;")
        (client.output / "fault-room-state.json").write_text(sql(client, f"SELECT id,name,type,(SELECT count(*) FROM memberships WHERE room_id=rooms.id) members FROM rooms WHERE id={fault_room}"), encoding="utf-8")
        # Timestamp ties are an explicit fixture, with matching text/FTS rows.
        timestamp = "2026-04-01 00:00:00.000000"
        statement = ""
        for offset in range(3):
            id = 9990000001 + offset
            statement += f"INSERT INTO messages(id,room_id,creator_id,client_message_id,created_at,updated_at) VALUES({id},{room},{labels['users.david']},'parity-chat-tie-{offset}','{timestamp}','{timestamp}'); INSERT INTO action_text_rich_texts(record_type,record_id,name,body,created_at,updated_at) VALUES('Message',{id},'body','parity-chat-tie-{offset}','{timestamp}','{timestamp}'); INSERT INTO message_search_index(rowid,body) VALUES({id},'parity-chat-tie-{offset}');"
        fixture_changes.append(statement)
        sql(client, statement)
        client.request("ties-before", "GET", f"/rooms/{room}/messages?before=9990000002")
        client.request("ties-after", "GET", f"/rooms/{room}/messages?after=9990000002")
        client.request("ties-around", "GET", f"/rooms/{room}/@9990000002")
        client.request("bot-query-regular-route", "GET", f"/rooms/{room}/messages?bot_key={labels['bot_keys.bender']}")
        bot = Client(instance, app, args.output)
        path = f"/rooms/{room}/{labels['bot_keys.bender']}/messages"
        record, _ = bot.request("bot-lifecycle-create", "POST", path, raw=b"parity-chat-lifecycle", headers={"Content-Type": "text/plain"})
        id = int(record["headers"]["location"].rsplit("/", 1)[-1])
        bot.request("bot-lifecycle-update", "PATCH", path + "/" + str(id), raw=b"<p>First</p><p>Second</p>", headers={"Content-Type": "text/plain"})
        bot.request("bot-boost-create", "POST", path + f"/{labels['messages.fourth']}/boosts", raw="👀".encode(), headers={"Content-Type": "text/plain"})
        boost_id = bot.records["bot-boost-create"]["summary"]["json"]["id"]
        bot.request("bot-boost-delete-own", "DELETE", path + f"/{labels['messages.fourth']}/boosts/{boost_id}")
        bot.request("bot-boost-delete-other", "DELETE", path + f"/{labels['messages.thirteenth']}/boosts/{labels['boosts.thirteenth']}")
        bot.request("bot-lifecycle-delete", "DELETE", path + "/" + str(id))
        bot.request("bot-edit-foreign", "PATCH", path + f"/{labels['messages.fourth']}", raw=b"forbidden", headers={"Content-Type": "text/plain"})
        bot.request("bot-delete-foreign", "DELETE", path + f"/{labels['messages.fourth']}")
        client.records.update(bot.records)
        client.snapshot("final-edge")
        (client.output / "fixture-changes.sql").write_text("\n".join(fixture_changes), encoding="utf-8")
        (client.output / "responses.json").write_text(json.dumps(client.records, indent=2) + "\n", encoding="utf-8")
        evidence[app] = {"cases": len(client.records), "fault_room_state": json.loads((client.output / "fault-room-state.json").read_text()), "bot_update_json": client.records["bot-lifecycle-update"]["summary"].get("json")}
    (args.output / "summary.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({app: values["cases"] for app, values in evidence.items()}))


if __name__ == "__main__":
    main()
