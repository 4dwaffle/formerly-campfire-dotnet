"""Check final sidebar application layout and exact pre-mutation message validators."""
import argparse
import json
from pathlib import Path
from differential import Client, Dom


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--instances', type=Path, required=True)
    parser.add_argument('--main', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    setup = json.loads(args.instances.read_text())
    if setup['family'] != 'chat':
        raise RuntimeError('Use the disposable chat family')
    args.output.mkdir(parents=True, exist_ok=False)
    records = {}
    for name, instance in setup['instances'].items():
        client = Client(instance, name, args.output)
        client.login('david', setup['labels'])
        record, body = client.request('sidebar-frame', 'GET', '/users/me/sidebar', headers={'Turbo-Frame': 'user_sidebar'})
        dom = Dom()
        dom.feed(body)
        records[name] = {'sidebar': {'status': record['status'], 'html': any(x['tag'] == 'html' for x in dom.elements), 'doctype': body.lstrip().lower().startswith('<!doctype html>'), 'csrf': any(x.get('name') == 'csrf-token' for x in dom.elements), 'importmap': any(x.get('type') == 'importmap' for x in dom.elements), 'frame': 'user_sidebar' in record['summary']['frame_ids']}, 'pages': {}}
        captures = json.loads((args.main / name / 'responses.json').read_text(encoding='utf-8'))
        if isinstance(captures, list):
            captures = {x['case']: x for x in captures}
        for case in ['david-message-page', 'david-message-page-before', 'david-message-page-after', 'david-page-if-none-match', 'david-page-if-modified-since']:
            capture = captures[case]
            records[name]['pages'][case] = {'status': capture['status'], 'etag': capture['headers'].get('etag'), 'last_modified': capture['headers'].get('last-modified')}
        (client.output / 'responses.json').write_text(json.dumps(client.records, indent=2) + '\n', encoding='utf-8')
    checks = []
    for app in ['dotnet', 'aot']:
        for section in ['sidebar', 'pages']:
            for key, expected in records['rails'][section].items():
                actual = records[app][section][key]
                checks.append({'app': app, 'name': section + ' ' + key, 'expected': expected, 'actual': actual, 'passed': actual == expected})
    result = {'assertions': len(checks), 'passed': sum(x['passed'] for x in checks), 'failed': sum(not x['passed'] for x in checks), 'results': records, 'checks': checks}
    (args.output / 'summary.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: result[k] for k in ['assertions', 'passed', 'failed']}))
    raise SystemExit(bool(result['failed']))


if __name__ == '__main__':
    main()
