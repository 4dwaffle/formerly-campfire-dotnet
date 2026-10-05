"""Capture real gzip HTML and fresh-CSRF behavior on the disposable chat family."""
import argparse
import gzip
import hashlib
import json
import re
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from differential import Client, Dom, summary
from edge_cases import sql


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--instances', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--extended', action='store_true', help='Also check pagination/search gzip and replay of a revoked session.')
    args = parser.parse_args()
    setup = json.loads(args.instances.read_text(encoding='utf-8'))
    if setup['family'] != 'chat':
        raise RuntimeError('Use only a disposable chat family')
    args.output.mkdir(parents=True, exist_ok=False)
    labels = setup['labels']
    room = labels['rooms.watercooler']
    checks, results = [], {}

    def check(app, name, expected, actual):
        checks.append({'app': app, 'name': name, 'expected': expected, 'actual': actual, 'passed': expected == actual})

    def get(client, case, path, encoding, headers=None):
        request = urllib.request.Request(client.base + path, headers={'Accept': 'text/html', 'Accept-Encoding': encoding, **(headers or {})})
        try:
            response = client.opener.open(request, timeout=45)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            wire = response.read()
            headers = {key.lower(): value for key, value in response.headers.items() if key.lower() != 'set-cookie'}
            (client.output / (case + '.wire')).write_bytes(wire)
            (client.output / (case + '-headers.json')).write_text(json.dumps({'status': response.status, 'headers': headers}, indent=2) + '\n')
            decoded = gzip.decompress(wire) if headers.get('content-encoding') == 'gzip' else wire
            record = {'case': case, 'method': 'GET', 'path': path, 'status': response.status, 'headers': headers,
                'wire_bytes': len(wire), 'decoded_bytes': len(decoded), 'wire_sha256': hashlib.sha256(wire).hexdigest(),
                'decoded_sha256': hashlib.sha256(decoded).hexdigest(), 'summary': summary(decoded)}
        (client.output / (case + '.wire')).write_bytes(wire)
        (client.output / (case + '.html')).write_bytes(decoded)
        client.records[case] = record
        return record, decoded.decode('utf-8')

    for app, instance in setup['instances'].items():
        david = Client(instance, app, args.output)
        david.login('david', labels)
        pages = {}
        paths = [('room', f'/rooms/{room}'), ('messages', f'/rooms/{room}/messages'), ('sidebar', '/users/me/sidebar')]
        if args.extended:
            message_room = json.loads(sql(david, f"SELECT room_id FROM messages WHERE id={labels['messages.busy_060']}"))[0]['room_id']
            paths.extend([('pagination', f"/rooms/{message_room}/messages?before={labels['messages.busy_060']}"), ('search', '/searches?q=coffee')])
        for name, path in paths:
            first, body = get(david, name + '-gzip-first', path, 'gzip')
            second, second_body = get(david, name + '-gzip-second', path, 'gzip')
            identity, _ = get(david, name + '-identity', path, 'identity')
            qzero, _ = get(david, name + '-gzip-qzero', path, 'gzip;q=0')
            check(app, name + ' real gzip responses', [200, 'gzip', 200, 'gzip'], [first['status'], first['headers'].get('content-encoding'), second['status'], second['headers'].get('content-encoding')])
            check(app, name + ' declared lengths match consumed bytes', True, all('content-length' not in item['headers'] or int(item['headers']['content-length']) == item['wire_bytes'] for item in [first, second]))
            check(app, name + ' warm and identity message IDs', first['summary']['message_ids'], second['summary']['message_ids'])
            check(app, name + ' decoded gzip agrees with identity IDs', first['summary']['message_ids'], identity['summary']['message_ids'])
            check(app, name + ' gzip zero quality remains identity', [200, None], [qzero['status'], qzero['headers'].get('content-encoding')])
            if args.extended:
                conditional, _ = get(david, name + '-gzip-conditional', path, 'gzip', {'If-None-Match': second['headers'].get('etag', '')})
                # Model-fresh message indexes can return304. Application
                # pages include newly masked CSRF tokens; pinned Rails sends
                # a new200 body even with the preceding exact response ETag.
                expected_status = 304 if name in ['messages', 'pagination'] else 200
                check(app, name + ' exact gzip validator', [expected_status, expected_status == 304], [conditional['status'], conditional['wire_bytes'] == 0])
            if name in ['room', 'sidebar']:
                one, two = Dom(), Dom()
                one.feed(body)
                two.feed(second_body)
                first_meta = next(item['content'] for item in one.elements if item.get('name') == 'csrf-token')
                second_meta = next(item['content'] for item in two.elements if item.get('name') == 'csrf-token')
                check(app, name + ' fresh CSRF metadata', True, first_meta != second_meta)
            if name == 'room':
                # The composer is outside message-fragment caching. Do not
                # infer freshness of every Rails cached partial's input.
                composer = re.search(r'<form\b[^>]*id="composer"[\s\S]*?</form>', body).group()
                composer_again = re.search(r'<form\b[^>]*id="composer"[\s\S]*?</form>', second_body).group()
                form_one, form_two = Dom(), Dom()
                form_one.feed(composer)
                form_two.feed(composer_again)
                token = next(item['value'] for item in form_one.elements if item.get('name') == 'authenticity_token')
                token_again = next(item['value'] for item in form_two.elements if item.get('name') == 'authenticity_token')
                check(app, 'room fresh composer CSRF token', True, token != token_again)
                david_token = second_meta
            pages[name] = {'first': first, 'second': second, 'identity': identity, 'qzero': qzero}
        jason = Client(instance, app, args.output)
        jason.login('jason', labels)
        _, body = get(jason, 'jason-room-gzip', f'/rooms/{room}', 'gzip')
        dom = Dom()
        dom.feed(body)
        jason_token = next(item['content'] for item in dom.elements if item.get('name') == 'csrf-token')
        message = labels['messages.mention']
        foreign, _ = jason.request('foreign-token-rejected', 'POST', f'/messages/{message}/boosts', fields={'authenticity_token': david_token, 'boost[content]': 'parity-gzip-forbidden'}, headers={'Accept': 'text/html'})
        check(app, 'foreign compressed-page token rejected', 422, foreign['status'])
        check(app, 'foreign token cannot persist a boost', 0, json.loads(sql(jason, "SELECT count(*) count FROM boosts WHERE content='parity-gzip-forbidden'"))[0]['count'])
        own, _ = jason.request('own-token-accepted', 'POST', f'/messages/{message}/boosts', fields={'authenticity_token': jason_token, 'boost[content]': 'parity-gzip-accepted'}, headers={'Accept': 'text/html'})
        check(app, 'own compressed-page token accepted', 302, own['status'])
        rows = json.loads(sql(jason, f"SELECT id FROM boosts WHERE message_id={message} AND booster_id={labels['users.jason']} AND content='parity-gzip-accepted'"))
        check(app, 'own token persists one real boost', 1, len(rows))
        if rows:
            jason.request('own-boost-cleanup', 'DELETE', f"/messages/{message}/boosts/{rows[0]['id']}", fields={'authenticity_token': jason_token}, headers={'Accept': 'text/html'})
        if args.extended:
            # Retain the original cookie explicitly, so this proves server-side
            # revocation rather than merely honoring the logout cookie expiry.
            processor = next(handler for handler in david.opener.handlers if isinstance(handler, urllib.request.HTTPCookieProcessor))
            replay = '; '.join(cookie.name + '=' + cookie.value for cookie in processor.cookiejar)
            logout, _ = david.request('logout', 'DELETE', '/session', headers={'X-CSRF-Token': david_token, 'Accept': 'text/html'})
            check(app, 'session logout accepted', 302, logout['status'])
            for name, path in [('room', f'/rooms/{room}'), ('messages', f'/rooms/{room}/messages')]:
                revoked, _ = get(david, name + '-revoked-session', path, 'gzip', {'Cookie': replay})
                check(app, name + ' revoked cookie denied', [302, '/session/new'], [revoked['status'], urllib.parse.urlsplit(revoked['headers'].get('location', '')).path])
        david.records.update(jason.records)
        (david.output / 'responses.json').write_text(json.dumps(david.records, indent=2) + '\n', encoding='utf-8')
        results[app] = pages
    result = {'instances': setup['instances'], 'assertions': len(checks), 'passed': sum(item['passed'] for item in checks), 'failed': sum(not item['passed'] for item in checks), 'checks': checks, 'results': results}
    (args.output / 'assertions.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: result[key] for key in ['assertions', 'passed', 'failed']}))
    raise SystemExit(bool(result['failed']))


if __name__ == '__main__':
    main()
