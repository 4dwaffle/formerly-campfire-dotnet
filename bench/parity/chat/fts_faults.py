"""Prove Rails callback commit boundaries using an isolated temporary FTS facade.

FTS virtual tables do not support regular abort triggers. Temporarily replace the
index with a populated ordinary table, then restore its original virtual table.
Only the disposable chat family may be changed. Every fixture change is recorded.
"""
import argparse
import json
from pathlib import Path
from differential import Client, HERE
from edge_cases import sql
from assertions import documents


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--instances', type=Path, default=HERE / 'runtime/instances.json')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    setup = json.loads(args.instances.read_text())
    if setup.get('family') not in ['chat', 'remediation-chat']:
        raise RuntimeError('Use the isolated chat family only')
    args.output.mkdir(parents=True, exist_ok=False)
    labels = setup['labels']
    room = labels['rooms.watercooler']
    results = {}
    for app, instance in setup['instances'].items():
        client = Client(instance, app, args.output)
        client.login('david', labels)
        changes = []
        targets = {}
        for name in ['update', 'delete']:
            record, _ = client.request('initial-' + name, 'POST', f'/rooms/{room}/messages', fields={'authenticity_token': client.csrf, 'message[body]': 'original-' + name, 'message[client_message_id]': 'parity-ftsfault-target-' + name}, headers={'Accept': 'text/vnd.turbo-stream.html'})
            targets[name] = record['summary']['message_ids'][0]
        replacement = "ALTER TABLE message_search_index RENAME TO parity_chat_original_fts; CREATE TABLE message_search_index(rowid INTEGER PRIMARY KEY,body TEXT); INSERT INTO message_search_index(rowid,body) SELECT rowid,body FROM parity_chat_original_fts; CREATE TRIGGER parity_chat_fts_insert BEFORE INSERT ON message_search_index WHEN NEW.body LIKE 'parity-ftsfault-%' BEGIN SELECT RAISE(ABORT,'parity-chat-ftsfault-insert'); END; CREATE TRIGGER parity_chat_fts_update BEFORE UPDATE ON message_search_index WHEN NEW.body LIKE 'parity-ftsfault-%' BEGIN SELECT RAISE(ABORT,'parity-chat-ftsfault-update'); END; CREATE TRIGGER parity_chat_fts_delete BEFORE DELETE ON message_search_index WHEN OLD.rowid=" + str(targets['delete']) + " BEGIN SELECT RAISE(ABORT,'parity-chat-ftsfault-delete'); END;"
        changes.append(replacement)
        sql(client, replacement)
        try:
            client.snapshot('before-fts-fault')
            client.request('create-index-fault', 'POST', f'/rooms/{room}/messages', fields={'authenticity_token': client.csrf, 'message[body]': 'parity-ftsfault-create', 'message[client_message_id]': 'parity-ftsfault-create'}, headers={'Accept': 'text/vnd.turbo-stream.html'})
            client.snapshot('after-create-fts-fault')
            client.request('update-index-fault', 'PATCH', f"/rooms/{room}/messages/{targets['update']}", fields={'authenticity_token': client.csrf, 'message[body]': 'parity-ftsfault-update'})
            client.snapshot('after-update-fts-fault')
            client.request('boost-create-index-fault', 'POST', f"/messages/{targets['update']}/boosts", fields={'authenticity_token': client.csrf, 'boost[content]': 'fts-fault-boost'}, headers={'Accept': 'text/vnd.turbo-stream.html'})
            boost_id = json.loads(sql(client, f"SELECT id FROM boosts WHERE message_id={targets['update']} AND content='fts-fault-boost'"))[0]['id']
            client.request('boost-delete-index-fault', 'DELETE', f"/messages/{targets['update']}/boosts/{boost_id}", fields={'authenticity_token': client.csrf}, headers={'Accept': 'text/vnd.turbo-stream.html'})
            client.request('delete-index-fault', 'DELETE', f"/rooms/{room}/messages/{targets['delete']}", fields={'authenticity_token': client.csrf}, headers={'Accept': 'text/vnd.turbo-stream.html'})
            client.snapshot('after-delete-fts-fault')
            statement = f"SELECT m.id,m.client_message_id,rt.body,idx.body indexed_body FROM messages m LEFT JOIN action_text_rich_texts rt ON rt.record_type='Message' AND rt.record_id=m.id AND rt.name='body' LEFT JOIN message_search_index idx ON idx.rowid=m.id WHERE m.client_message_id LIKE 'parity-ftsfault-%' ORDER BY m.id;"
            state = json.loads(sql(client, statement))
            (client.output / 'fault-message-state.json').write_text(json.dumps(state, indent=2) + '\n')
            deleted_index = json.loads(sql(client, f"SELECT body FROM message_search_index WHERE rowid={targets['delete']}"))
            before = documents(client.output / 'before-fts-fault-database.jsonl')
            after = documents(client.output / 'after-create-fts-fault-database.jsonl')
            memberships = lambda snapshot: next(rows for rows in snapshot if rows and 'involvement' in rows[0])
            rooms = lambda snapshot: next(rows for rows in snapshot if rows and 'type' in rows[0])
            room_changed = next(x['updated_at'] for x in rooms(before) if x['id'] == room) != next(x['updated_at'] for x in rooms(after) if x['id'] == room)
            results[app] = {'responses': {name: client.records[name]['status'] for name in ['create-index-fault', 'update-index-fault', 'delete-index-fault', 'boost-create-index-fault', 'boost-delete-index-fault']}, 'messages': state, 'deleted_index': deleted_index, 'unread_unchanged_on_index_failure': memberships(before) == memberships(after), 'room_touch_committed': room_changed, 'boost_removed': json.loads(sql(client, f"SELECT count(*) count FROM boosts WHERE id={boost_id}"))[0]['count'] == 0}
        finally:
            restore = "DROP TRIGGER parity_chat_fts_insert; DROP TRIGGER parity_chat_fts_update; DROP TRIGGER parity_chat_fts_delete; DROP TABLE message_search_index; ALTER TABLE parity_chat_original_fts RENAME TO message_search_index; DELETE FROM message_search_index WHERE rowid NOT IN (SELECT id FROM messages); DELETE FROM message_search_index WHERE rowid IN (SELECT id FROM messages WHERE client_message_id LIKE 'parity-ftsfault-%'); INSERT INTO message_search_index(rowid,body) SELECT m.id,coalesce(rt.body,'') FROM messages m LEFT JOIN action_text_rich_texts rt ON rt.record_type='Message' AND rt.record_id=m.id AND rt.name='body' WHERE m.client_message_id LIKE 'parity-ftsfault-%';"
            changes.append(restore)
            sql(client, restore)
            (client.output / 'fixture-changes.sql').write_text('\n'.join(changes), encoding='utf-8')
            (client.output / 'responses.json').write_text(json.dumps(client.records, indent=2) + '\n', encoding='utf-8')
    (args.output / 'summary.json').write_text(json.dumps(results, indent=2) + '\n', encoding='utf-8')
    def normalized(value):
        return {**value, 'messages': [{k: v for k, v in row.items() if k != 'id'} for row in value['messages']]}
    checks = [{'app': app, 'name': name, 'passed': actual == normalized(results['rails'])[name], 'expected': normalized(results['rails'])[name], 'actual': actual} for app in ['dotnet', 'aot'] for name, actual in normalized(results[app]).items()]
    assertions = {'assertions': len(checks), 'passed': sum(x['passed'] for x in checks), 'failed': sum(not x['passed'] for x in checks), 'checks': checks}
    (args.output / 'assertions.json').write_text(json.dumps(assertions, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({**results, 'assertions': {k: v for k, v in assertions.items() if k != 'checks'}}))
    raise SystemExit(1 if assertions['failed'] else 0)


if __name__ == '__main__':
    main()
