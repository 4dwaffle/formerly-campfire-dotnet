"""Verify the previously observed broader status and message-frame layout differences."""
import argparse
import json
from pathlib import Path
from differential import Dom


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--main', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    captures = {app: json.loads((args.main / app / 'responses.json').read_text(encoding='utf-8')) for app in ['rails', 'dotnet', 'aot']}
    checks = []
    for app in ['dotnet', 'aot']:
        for case in ['david-room-invalid-id', 'fixture-unrenderable', 'fixture-edit-unrenderable', 'loner-search']:
            actual = captures[app][case]['status']
            expected = captures['rails'][case]['status']
            checks.append({'app': app, 'name': case + ' status', 'expected': expected, 'actual': actual, 'passed': actual == expected})
        for case in ['frame-message-show', 'frame-message-edit', 'fixture-edit-unrenderable']:
            values = {}
            for name in ['rails', app]:
                body = (args.main / name / (case + '.body')).read_text(encoding='utf-8')
                dom = Dom()
                dom.feed(body)
                values[name] = {'doctype': body.lstrip().lower().startswith('<!doctype html>'), 'importmap': any(x.get('type') == 'importmap' for x in dom.elements), 'csrf_meta': any(x.get('name') == 'csrf-token' for x in dom.elements), 'title': captures[name][case]['summary']['title']}
            checks.append({'app': app, 'name': case + ' layout', 'expected': values['rails'], 'actual': values[app], 'passed': values['rails'] == values[app]})
        for case, token in [('fixture-unrenderable', 'Failed to load message content'), ('fixture-edit-unrenderable', 'This message lost its author.')]:
            values = {name: token in (args.main / name / (case + '.body')).read_text(encoding='utf-8') for name in ['rails', app]}
            checks.append({'app': app, 'name': case + ' content', 'expected': values['rails'], 'actual': values[app], 'passed': values['rails'] == values[app]})
    result = {'assertions': len(checks), 'passed': sum(x['passed'] for x in checks), 'failed': sum(not x['passed'] for x in checks), 'checks': checks}
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: result[k] for k in ['assertions', 'passed', 'failed']}))
    raise SystemExit(bool(result['failed']))


if __name__ == '__main__':
    main()
