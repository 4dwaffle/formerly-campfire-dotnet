"""Verify source-traced parameter, format, plaintext and callback branches."""
import argparse
import json
from pathlib import Path
from differential import Client
from edge_cases import sql


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--instances', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    setup = json.loads(args.instances.read_text())
    if setup.get('family') not in ['chat', 'remediation-chat']:
        raise RuntimeError('Use only the isolated chat family')
    args.output.mkdir(parents=True, exist_ok=False)
    labels = setup['labels']
    room = labels['rooms.watercooler']
    vectors = json.loads((Path(__file__).parent / 'plaintext-content-rails-vectors.json').read_text(encoding='utf-8'))
    results = {}
    for app, instance in setup['instances'].items():
        human = Client(instance, app, args.output)
        human.login('david', labels)
        messages = f'/rooms/{room}/messages'
        human.request('human-create-html', 'POST', messages, fields={'authenticity_token': human.csrf, 'message[body]': 'source html', 'message[client_message_id]': 'parity-chat-source-html'}, headers={'Accept': 'text/html'})
        id = json.loads(sql(human, "SELECT id FROM messages WHERE client_message_id='parity-chat-source-html' ORDER BY id DESC LIMIT 1"))[0]['id']
        human.request('human-update-json-null', 'PATCH', messages + '/' + str(id), raw=b'{"message":{"body":null}}', headers={'Content-Type': 'application/json', 'Accept': 'text/html', 'X-CSRF-Token': human.csrf})
        null_state = json.loads(sql(human, f"SELECT body FROM action_text_rich_texts WHERE record_type='Message' AND record_id={id}"))[0]['body']
        human.request('human-delete-html', 'DELETE', messages + '/' + str(id), headers={'Accept': 'text/html', 'X-CSRF-Token': human.csrf})
        deletion_state = json.loads(sql(human, f'SELECT count(*) count FROM messages WHERE id={id}'))[0]['count']
        human.request('human-invalid-signature', 'POST', messages, fields={'authenticity_token': human.csrf, 'message[attachment]': 'invalid-token'})
        human.request('human-page-turbo-suffix', 'GET', messages + '.turbo_stream')
        human.request('refresh-html', 'GET', f'/rooms/{room}/refresh?since=0', headers={'Accept': 'text/html'})
        human.request('refresh-json', 'GET', f'/rooms/{room}/refresh?since=0', headers={'Accept': 'application/json'})
        human.request('refresh-turbo', 'GET', f'/rooms/{room}/refresh?since=0', headers={'Accept': 'text/vnd.turbo-stream.html'})
        human.request('anchors-before-wins', 'GET', messages + '?before=' + str(labels['messages.busy_060']) + '&after=garbage')
        human.request('autocomplete-blank-query', 'GET', '/autocompletable/users.json?query=%20%20')
        human.request('search-post-invalid-operator', 'POST', '/searches', fields={'authenticity_token': human.csrf, 'q': 'OR'})
        human.request('search-post-missing', 'POST', '/searches', fields={'authenticity_token': human.csrf})
        changes = f"CREATE TRIGGER fail_chat_open_grant BEFORE INSERT ON memberships WHEN NEW.user_id={labels['users.kevin']} AND EXISTS(SELECT 1 FROM rooms WHERE id=NEW.room_id AND name='parity-chat-open-grant-failure') BEGIN SELECT RAISE(ABORT,'parity open grant'); END;"
        sql(human, changes)
        try:
            human.request('open-grant-failure', 'POST', '/rooms/opens', fields={'authenticity_token': human.csrf, 'room[name]': 'parity-chat-open-grant-failure'})
            open_state = json.loads(sql(human, "SELECT name,(SELECT group_concat(user_id) FROM memberships WHERE room_id=r.id) members FROM rooms r WHERE name='parity-chat-open-grant-failure' ORDER BY id DESC LIMIT 1"))
        finally:
            sql(human, 'DROP TRIGGER fail_chat_open_grant;')
            (human.output / 'fixture-changes.sql').write_text(changes + '\nDROP TRIGGER fail_chat_open_grant;\n', encoding='utf-8')
        bot = Client(instance, app, args.output)
        bot_path = f"/rooms/{room}/{labels['bot_keys.bender']}/messages"
        record, _ = bot.request('bot-blank-attachment', 'POST', bot_path + '?attachment=', raw=b'ignored raw text', headers={'Content-Type': 'text/plain'})
        bot_id = record['headers']['location'].rsplit('/', 1)[-1]
        blank_attachment = json.loads(sql(human, f"SELECT coalesce((SELECT body FROM action_text_rich_texts WHERE record_type='Message' AND record_id={bot_id}), '') body"))[0]['body']
        bot.request('bot-index-html', 'GET', bot_path + '.html')
        bot.request('bot-index-stream', 'GET', bot_path + '.turbo_stream')
        bot.request('bot-update-html', 'PATCH', bot_path + '/' + bot_id + '.html', raw=b'updated', headers={'Content-Type': 'text/plain'})
        bot.request('bot-boost-html', 'POST', bot_path + '/' + bot_id + '/boosts.html', raw=b'persisted boost', headers={'Content-Type': 'text/plain'})
        plaintext = []
        for index, vector in enumerate(vectors):
            record, _ = bot.request('plaintext-' + str(index), 'PATCH', bot_path + '/' + bot_id, raw=vector['html'].encode(), headers={'Content-Type': 'text/plain'})
            plaintext.append(record['summary']['json']['body']['plain_text'])
        human.records.update(bot.records)
        (human.output / 'responses.json').write_text(json.dumps(human.records, indent=2) + '\n', encoding='utf-8')
        results[app] = {'statuses': {name: record['status'] for name, record in human.records.items() if name not in ['david-login-form', 'david-login', 'david-home', 'david-home-room']}, 'null_body': null_state, 'deleted_message_count': deletion_state, 'open_grant_state': open_state, 'blank_bot_attachment_body': blank_attachment, 'plaintext': plaintext, 'before_anchor_ids': human.records['anchors-before-wins']['summary']['message_ids'], 'blank_autocomplete_ids': [u['value'] for u in human.records['autocomplete-blank-query']['summary']['json']]}
    checks = [{'app': app, 'name': name, 'passed': actual == results['rails'][name], 'expected': results['rails'][name], 'actual': actual} for app in ['dotnet', 'aot'] for name, actual in results[app].items()]
    # Also assert Rails runtime agrees with independently captured converter vectors.
    checks.append({'app': 'rails', 'name': 'plaintext converter vectors', 'passed': results['rails']['plaintext'] == [v['expected'] for v in vectors], 'expected': [v['expected'] for v in vectors], 'actual': results['rails']['plaintext']})
    result = {'assertions': len(checks), 'passed': sum(c['passed'] for c in checks), 'failed': sum(not c['passed'] for c in checks), 'checks': checks, 'results': results}
    (args.output / 'assertions.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in result.items() if k not in ['checks', 'results']}))
    raise SystemExit(1 if result['failed'] else 0)


if __name__ == '__main__':
    main()
