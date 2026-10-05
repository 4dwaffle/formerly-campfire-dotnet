"""Observe the pinned production fragment cache's dependency policy, without fixing it."""
import argparse
import html
import json
import re
import subprocess
from pathlib import Path
from differential import Client
from edge_cases import sql


def literal(value):
    return 'NULL' if value is None else "'" + str(value).replace("'", "''") + "'"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--instances', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    setup = json.loads(args.instances.read_text(encoding='utf-8'))
    if setup['family'] != 'chat':
        raise RuntimeError('Use only the disposable chat family')
    args.output.mkdir(parents=True, exist_ok=False)
    labels = setup['labels']
    room = labels['rooms.watercooler']
    old_body = 'PARITY-CACHE-OLD'
    new_body = 'PARITY-CACHE-NEW'
    new_name = 'PARITY-CREATOR-NEW'
    client_id = 'parity-cache-dependencies-target'
    results = {}
    for app, instance in setup['instances'].items():
        client = Client(instance, app, args.output)
        client.login('david', labels)
        record, _ = client.request('cache-target-create', 'POST', f'/rooms/{room}/messages', fields={'authenticity_token': client.csrf, 'message[body]': '<p>' + old_body + '</p>', 'message[client_message_id]': client_id}, headers={'Accept': 'text/vnd.turbo-stream.html'})
        if record['status'] != 200 or len(record['summary']['message_ids']) != 1:
            raise RuntimeError(app + ' could not create cache target')
        id = int(record['summary']['message_ids'][0])
        state_sql = f"SELECT m.updated_at message_version,m.client_message_id,rt.body,rt.updated_at body_version,u.id creator_id,u.name creator_name,u.updated_at creator_version,r.updated_at room_version,(SELECT body FROM message_search_index WHERE rowid=m.id) indexed_body,(SELECT group_concat(user_id||':'||coalesce(unread_at,'')||':'||updated_at,'|') FROM (SELECT * FROM memberships WHERE room_id=m.room_id ORDER BY id)) unread_state,(SELECT blob_id FROM active_storage_attachments WHERE record_type='Message' AND record_id=m.id AND name='attachment') blob_id FROM messages m JOIN action_text_rich_texts rt ON rt.record_type='Message' AND rt.record_id=m.id AND rt.name='body' JOIN users u ON u.id=m.creator_id JOIN rooms r ON r.id=m.room_id WHERE m.id={id}"
        initial = json.loads(sql(client, state_sql))[0]
        changes = []
        observed = {}
        def capture(stage):
            record, body = client.request(stage, 'GET', f'/rooms/{room}/messages', headers={'Accept': 'text/html'})
            match = re.search(r'<div\b[^>]*\bid="message_' + re.escape(client_id) + r'"', body)
            if record['status'] != 200 or match is None:
                raise RuntimeError(app + ' target missing from ' + stage)
            # This newly created target is last in the ascending page. Retain
            # its rendered tail so unrelated creator names cannot affect checks.
            fragment = body[match.start():]
            (client.output / (stage + '-fragment.html')).write_text(fragment, encoding='utf-8')
            observed[stage] = {'old_body': old_body in fragment, 'new_body': new_body in fragment,
                'old_creator': html.escape(initial['creator_name'], quote=True) in fragment,
                'new_creator': new_name in fragment,
                'model_etag': record['headers'].get('etag')}
        try:
            capture('cache-warm-first')
            capture('cache-warm-second')
            rename = f"UPDATE users SET name={literal(new_name)},updated_at='2030-01-01 00:00:00.123456' WHERE id={initial['creator_id']};"
            changes.append(rename)
            sql(client, rename)
            capture('cache-after-creator-rename')
            body_edit = f"UPDATE action_text_rich_texts SET body={literal('<p>' + new_body + '</p>')} WHERE record_type='Message' AND record_id={id} AND name='body';"
            changes.append(body_edit)
            sql(client, body_edit)
            capture('cache-after-same-version-body-edit')
            final = json.loads(sql(client, state_sql))[0]
            unchanged, _ = client.request('cache-no-op-update', 'PATCH', f'/rooms/{room}/messages/{id}', fields={'authenticity_token': client.csrf, 'message[body]': '<p>' + new_body + '</p>'}, headers={'Accept': 'text/html'})
            if unchanged['status'] != 302:
                raise RuntimeError(app + ' cache target no-op update failed')
            capture('cache-after-no-op-update')
            after_noop = json.loads(sql(client, state_sql))[0]
            unchanged_fields, _ = client.request('cache-no-op-client-and-detach', 'PATCH', f'/rooms/{room}/messages/{id}', fields={'authenticity_token': client.csrf, 'message[client_message_id]': client_id, 'message[attachment]': ''}, headers={'Accept': 'text/html'})
            if unchanged_fields['status'] != 302:
                raise RuntimeError(app + ' unchanged client/empty detach failed')
            after_noop_fields = json.loads(sql(client, state_sql))[0]
            # A genuinely different body is needed to trigger Action Text's
            # dirty autosave/touch; the prior final-04 same-body PATCH did not.
            touched, _ = client.request('cache-edit-message', 'PATCH', f'/rooms/{room}/messages/{id}', fields={'authenticity_token': client.csrf, 'message[body]': '<p>' + new_body + ' EDITED</p>'}, headers={'Accept': 'text/html'})
            if touched['status'] != 302:
                raise RuntimeError(app + ' cache target edit failed')
            capture('cache-after-message-touch')
            after_touch = json.loads(sql(client, state_sql))[0]
            changed_client, _ = client.request('cache-changed-client', 'PATCH', f'/rooms/{room}/messages/{id}', fields={'authenticity_token': client.csrf, 'message[client_message_id]': client_id + '-changed'}, headers={'Accept': 'text/html'})
            if changed_client['status'] != 302:
                raise RuntimeError(app + ' changed client update failed')
            after_client = json.loads(sql(client, state_sql))[0]
            results[app] = {'initial': initial, 'final': final, 'observations': observed,
                'after_noop': after_noop, 'noop_changed_message_version': final['message_version'] != after_noop['message_version'],
                'noop_changed_body_version': final['body_version'] != after_noop['body_version'],
                'after_noop_fields': after_noop_fields, 'noop_state_unchanged': final == after_noop,
                'noop_non_index_state_unchanged': {k: v for k, v in final.items() if k != 'indexed_body'} == {k: v for k, v in after_noop.items() if k != 'indexed_body'},
                'noop_indexed': after_noop['indexed_body'] == new_body,
                'noop_fields_state_unchanged': after_noop == after_noop_fields,
                'after_touch': after_touch, 'touch_changed_message_version': after_noop['message_version'] != after_touch['message_version'],
                'real_edit_body_touched': after_noop['body_version'] != after_touch['body_version'],
                'real_edit_room_touched': after_noop['room_version'] != after_touch['room_version'],
                'real_edit_indexed': after_touch['indexed_body'] == new_body + ' EDITED',
                'real_edit_unread_unchanged': after_noop['unread_state'] == after_touch['unread_state'],
                'after_client': after_client, 'client_changed_message_version': after_touch['message_version'] != after_client['message_version'],
                'client_changed_body_version': after_touch['body_version'] != after_client['body_version'],
                'message_version_unchanged': initial['message_version'] == final['message_version'],
                'rich_text_version_unchanged': initial['body_version'] == final['body_version']}
        finally:
            restore = f"UPDATE users SET name={literal(initial['creator_name'])},updated_at={literal(initial['creator_version'])} WHERE id={initial['creator_id']}; UPDATE action_text_rich_texts SET body={literal(initial['body'])},updated_at={literal(initial['body_version'])} WHERE record_type='Message' AND record_id={id} AND name='body';"
            changes.append(restore)
            sql(client, restore)
            (client.output / 'fixture-changes.sql').write_text('\n'.join(changes) + '\n', encoding='utf-8')
            (client.output / 'responses.json').write_text(json.dumps(client.records, indent=2) + '\n', encoding='utf-8')
    # Read actual production cache configuration and prove a local roundtrip.
    # No URL fetches, outbound notifications, or reference checkout edits occur.
    ruby = "key='parity-chat-dependency-probe'; Rails.cache.write(key,123); value=Rails.cache.read(key); Rails.cache.delete(key); puts JSON.generate({perform_caching: Rails.application.config.action_controller.perform_caching, cache_class: Rails.cache.class.name, cache_roundtrip: value})"
    command = ['docker', 'exec', setup['instances']['rails']['container'], 'bin/rails', 'runner', ruby]
    process = subprocess.run(command, capture_output=True, text=True, encoding='utf-8', check=True)
    (args.output / 'rails-cache-runner.txt').write_text(process.stdout + process.stderr, encoding='utf-8')
    cache_config = json.loads(process.stdout.strip().splitlines()[-1])
    result = {'instances': setup['instances'], 'rails_cache_configuration': cache_config, 'results': results}
    (args.output / 'summary.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'rails_cache_configuration': cache_config, 'observations': {app: value['observations'] for app, value in results.items()}}))


if __name__ == '__main__':
    main()
