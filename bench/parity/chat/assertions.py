"""Evaluate selected semantic parity assertions against saved evidence; no HTTP."""
import json
import argparse
from pathlib import Path

HERE = Path(__file__).resolve().parent


def documents(path):
    text = path.read_text(encoding='utf-8')
    decoder = json.JSONDecoder()
    result = []
    while text.strip():
        value, end = decoder.raw_decode(text.lstrip())
        result.append(value)
        text = text.lstrip()[end:]
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--main', type=Path, default=HERE / 'runtime/differential-03')
    parser.add_argument('--edges', type=Path, default=HERE / 'runtime/edges-01')
    parser.add_argument('--follow', type=Path, default=HERE / 'runtime/followups-01')
    parser.add_argument('--output', type=Path, default=HERE / 'assertions.json')
    parser.add_argument('--extended', action='store_true', help='Compare additional selected response IDs and original controls')
    args = parser.parse_args()
    roots = {'main': args.main, 'edges': args.edges, 'follow': args.follow}
    data = {stage: {app: json.loads((root / app / 'responses.json').read_text()) for app in ['rails', 'dotnet', 'aot']} for stage, root in roots.items()}
    checks = []

    def check(name, stage, case, extract):
        expected = extract(data[stage]['rails'][case])
        for app in ['dotnet', 'aot']:
            actual = extract(data[stage][app][case])
            checks.append(dict(name=name, app=app, expected=expected, actual=actual, passed=actual == expected, evidence=f'runtime/{roots[stage].name}/{app}/{case}.body'))

    status = lambda r: r['status']
    for case in ['anonymous-room', 'david-message-page', 'david-message-page-before', 'david-message-page-after', 'david-page-if-none-match', 'create-formatting', 'create-blank', 'update-created', 'delete-created', 'create-room-closed', 'create-room-direct', 'create-room-direct-reuse', 'involvement-hide', 'involvement-restore', 'jz-update-foreign-message', 'kevin-update-foreign-message', 'loner-page', 'loner-show-message', 'loner-autocomplete-room', 'loner-post-inaccessible-room', 'bot-invalid-key', 'bot-inaccessible-room', 'bot-create-text', 'bot-create-blank']:
        check(case + ' status', 'main', case, status)
    for case in ['bot-lifecycle-create', 'bot-lifecycle-update', 'bot-lifecycle-delete', 'bot-boost-create', 'bot-boost-delete-own', 'bot-boost-delete-other', 'bot-edit-foreign', 'bot-delete-foreign']:
        check(case + ' status', 'edges', case, status)
    for case in ['create-empty-params', 'create-json', 'david-message-top-level', 'david-autocomplete-json-suffix', 'david-autocomplete-invalid-room', 'david-message-page-invalid-before', 'david-message-json-accept', 'david-settings', 'involvement-invalid', 'bot-create-curl-default', 'bot-boost-curl-default', 'david-sidebar-other-id', 'jz-delete-shared-room-as-direct', 'david-search-invalid-operator']:
        check(case + ' status', 'main', case, status)
    for case in ['future-if-modified-since', 'opens-index', 'closeds-index', 'directs-index', 'direct-subtype-show', 'unauthenticated-bot-query-regular-route']:
        check(case + ' status', 'follow', case, status)
    check('autocomplete empty filter falls back to query', 'main', 'david-autocomplete-empty-filter', lambda r: [u['value'] for u in r['summary']['json']])
    check('legacy mention remains visible', 'main', 'fixture-mention_marshal', lambda r: 'David' in r['summary']['presentations'][0].split('that was')[0])
    check('cleared attachment disappears', 'main', 'clear-attachment-after', lambda r: sum('attachment' in str(image) for image in r['summary']['images']))
    check('own-host preview image removed', 'main', 'create-same-host-preview', lambda r: any('/rooms/' in image.get('src', '') for image in r['summary']['images']))
    check('IP preview image removed', 'edges', 'ip-preview-create', lambda r: any('127.0.0.1' in image.get('src', '') for image in r['summary']['images']))
    check('solo preview removes duplicate URL text', 'edges', 'solo-legacy-create', lambda r: '>https://basecamp.com/</a>' in r['summary']['presentations'][0] or '>https://basecamp.com/<' in r['summary']['presentations'][0])
    check('edit value rebuilds handwritten attachment safely', 'edges', 'handwritten-content-edit', lambda r: 'parityPwn()' in r['summary']['editors'][0]['value'])
    check('edit value includes reconstructed mention content', 'main', 'fixture-edit-mention_marshal', lambda r: 'content=' in r['summary']['editors'][0]['value'])
    check('editor preserves input behavior data actions', 'main', 'fixture-edit-mention_marshal', lambda r: r['summary']['editors'][0].get('data-action'))
    check('bot plaintext paragraph separation', 'edges', 'bot-lifecycle-update', lambda r: r['summary']['json']['body']['plain_text'])
    check('bot timestamp uses milliseconds', 'edges', 'bot-lifecycle-update', lambda r: len(r['summary']['json']['created_at'].split('.')[1].rstrip('Z')))
    check('unsafe URI/event/script removed in presentation', 'main', 'create-unsafe-uri', lambda r: any(token in r['summary']['presentations'][0] for token in ['javascript:', 'onmouseover=', '<script']))
    check('direct room reuse preserves same location', 'main', 'create-room-direct-reuse', lambda r: r['headers']['location'].rsplit('/', 1)[-1])
    if args.extended:
        for case in ['david-message-page', 'david-message-page-before', 'david-message-page-after', 'david-room-around', 'david-search-coffee']:
            check(case + ' exact message IDs', 'main', case, lambda r: r['summary']['message_ids'])
        for case in ['david-room', 'david-open-new', 'david-closed-new', 'david-direct-edit']:
            check(case + ' page title', 'main', case, lambda r: r['summary']['title'])
        check('edit direct upload metadata', 'main', 'fixture-edit-mention_marshal', lambda r: sorted(k for k in r['summary']['editors'][0] if k in ['data-direct-upload-url', 'data-blob-url-template']))
    for app in ['dotnet', 'aot']:
        state = json.loads((roots['edges'] / app / 'fault-room-state.json').read_text())[0]
        expected = json.loads((roots['edges'] / 'rails/fault-room-state.json').read_text())[0]
        checks.append(dict(name='room rename commit before failed membership revision', app=app, expected=expected['name'], actual=state['name'], passed=expected['name'] == state['name'], evidence=f'runtime/{roots["edges"].name}/{app}/fault-room-state.json'))
        snapshots = documents(roots['main'] / app / 'after-human-mutations-database.jsonl')
        counts = snapshots[0][0]
        checks.append(dict(name='message and FTS row counts remain consistent', app=app, expected=True, actual=counts['messages'] == counts['fts'], passed=counts['messages'] == counts['fts'], evidence=f'runtime/{roots["main"].name}/{app}/after-human-mutations-database.jsonl'))
    result = {'assertions': len(checks), 'passed': sum(c['passed'] for c in checks), 'failed': sum(not c['passed'] for c in checks), 'by_app': {app: {'passed': sum(c['passed'] for c in checks if c['app'] == app), 'failed': sum(not c['passed'] for c in checks if c['app'] == app)} for app in ['dotnet', 'aot']}, 'checks': checks}
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: value for key, value in result.items() if key != 'checks'}))
    raise SystemExit(1 if result['failed'] else 0)


if __name__ == '__main__':
    main()
