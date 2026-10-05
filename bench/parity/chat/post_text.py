"""Compare text POST, persisted FTS and rendered create results with pinned Rails."""
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
    setup = json.loads(args.instances.read_text(encoding='utf-8-sig'))
    if setup['family'] != 'chat':
        raise RuntimeError('Use only an isolated chat family')
    args.output.mkdir(parents=True, exist_ok=False)
    labels, observations = setup['labels'], {}
    room = labels['rooms.watercooler']
    vectors = [('unicode', 'plain Čau ☕\nsecond line', 'plain Čau ☕\nsecond line'),
               ('ascii-strip', ' \tplain\ntext\v ', 'plain\ntext'),
               ('unicode-space', '\u00a0plain\u00a0', '\u00a0plain\u00a0'),
               ('cr', 'a\r\nb\rc', 'a\nb\nc'),
               ('entities', 'one &amp; two &#x2615;', 'one & two ☕')]
    checks = []
    def check(app, name, expected, actual):
        checks.append({'app': app, 'name': name, 'expected': expected, 'actual': actual, 'passed': expected == actual})
    for app, instance in setup['instances'].items():
        human = Client(instance, app, args.output)
        human.login('david', labels)
        bot = Client(instance, app, args.output)
        observations[app] = {}
        for name, body, expected in vectors:
            for mode in ['human', 'bot']:
                case = mode + '-' + name
                client_id = 'parity-posttext-' + args.output.name + '-' + name
                if mode == 'human':
                    response, html = human.request(case, 'POST', f'/rooms/{room}/messages', fields={
                        'authenticity_token': human.csrf, 'message[body]': body, 'message[client_message_id]': client_id},
                        headers={'Accept': 'text/vnd.turbo-stream.html'})
                    state = json.loads(sql(human, f"SELECT m.id,m.client_message_id,rt.body,idx.body indexed_body FROM messages m LEFT JOIN action_text_rich_texts rt ON rt.record_type='Message' AND rt.record_id=m.id AND rt.name='body' LEFT JOIN message_search_index idx ON idx.rowid=m.id WHERE m.client_message_id='{client_id}'"))[0]
                    check(app, case + ' Turbo create contract', [200, True, True], [response['status'], '<turbo-stream action="append"' in html, 'id="message_' + client_id + '"' in html])
                else:
                    # A bot request has no human session cookie. Rails chooses
                    # the cookie user first, whose raw POST still needs CSRF.
                    response, _ = bot.request(case, 'POST', f"/rooms/{room}/{labels['bot_keys.bender']}/messages", raw=body.encode('utf-8'), headers={'Content-Type': 'text/plain', 'Accept': 'application/json'})
                    check(app, case + ' bot create status', 201, response['status'])
                    message_id = int(response['headers']['location'].rsplit('/', 1)[1])
                    state = json.loads(sql(human, f"SELECT m.id,m.client_message_id,rt.body,idx.body indexed_body FROM messages m LEFT JOIN action_text_rich_texts rt ON rt.record_type='Message' AND rt.record_id=m.id AND rt.name='body' LEFT JOIN message_search_index idx ON idx.rowid=m.id WHERE m.id={message_id}"))[0]
                check(app, case + ' indexed plaintext', expected, state['indexed_body'])
                rendered, html = human.request(case + '-rendered', 'GET', f"/rooms/{room}/messages/{state['id']}", headers={'Accept': 'text/html'})
                check(app, case + ' persisted rendering', [200, True], [rendered['status'], 'data-message-id="' + str(state['id']) + '"' in html])
                observations[app][case] = state
        human.records.update(bot.records)
        (human.output / 'responses.json').write_text(json.dumps(human.records, indent=2) + '\n')
    for app in ['dotnet', 'aot']:
        for case, actual in observations[app].items():
            # Keep exact stored bodies as evidence; comparison does not hide CR
            # normalization or assume equal timestamp/UUID serialization.
            expected = observations['rails'][case]
            check(app, case + ' original stored body', expected['body'], actual['body'])
    result = {'assertions': len(checks), 'passed': sum(check['passed'] for check in checks),
              'failed': sum(not check['passed'] for check in checks), 'checks': checks, 'results': observations}
    (args.output / 'assertions.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({key: result[key] for key in ['assertions', 'passed', 'failed']}))
    raise SystemExit(bool(result['failed']))


if __name__ == '__main__':
    main()
