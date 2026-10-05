"""Derive a portable gate manifest from saved Chat differential captures."""
import argparse
import hashlib
import json
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--suffix', required=True)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--gates', default='assertions,broader-assertions,source-assertions,frame-assertions,fts-assertions,cache-assertions,compressed-assertions')
    parser.add_argument('--historical-gates', default='', help='Retained checker/precondition gates excluded from final assertion totals.')
    args = parser.parse_args()
    root = Path(__file__).parent
    manifest = root / f'instances-{args.suffix}.json'
    setup = json.loads(manifest.read_text(encoding='utf-8-sig'))
    gates = []
    for prefix in args.gates.split(','):
        path = root / f'{prefix}-{args.suffix}.json'
        gate = json.loads(path.read_text(encoding='utf-8-sig'))
        gates.append({'file': path.name, 'sha256': digest(path),
                      **{key: gate[key] for key in ['assertions', 'passed', 'failed']}})
    captures = []
    for path in sorted(args.runtime.rglob('responses.json')):
        records = json.loads(path.read_text(encoding='utf-8-sig'))
        captures.append({'suite': path.parent.parent.name, 'app': path.parent.name,
                         'count': len(records), 'statuses': dict(sorted(Counter(
                             str(record['status']) for record in records.values()).items())),
                         'sha256': digest(path), 'raw_path': path.as_posix()})
    cache = root / f'cache-policy-{args.suffix}.json'
    incomplete = []
    for path in sorted(args.runtime.rglob('*.body')):
        if not (path.parent / 'responses.json').exists():
            incomplete.append({'raw_path': path.as_posix(), 'sha256': digest(path), 'bytes': path.stat().st_size,
                               'note': 'Aborted probe retained a consumed response body without its complete status/header record.'})
    historical = []
    for prefix in filter(None, args.historical_gates.split(',')):
        path = root / f'{prefix}-{args.suffix}.json'
        gate = json.loads(path.read_text(encoding='utf-8-sig'))
        historical.append({'file': path.name, 'sha256': digest(path),
                           **{key: gate[key] for key in ['assertions', 'passed', 'failed']}})
    result = {
        'checked_utc': datetime.now(timezone.utc).isoformat(),
        'manifest': manifest.name, 'manifest_sha256': digest(manifest),
        'reported_runtime_source_sha256': setup['reported_runtime_source_sha256'],
        'images': setup['instances'], 'gates': gates,
        'historical_checker_or_precondition_gates': historical,
        **{key: sum(gate[key] for gate in gates) for key in ['assertions', 'passed', 'failed']},
        'response_records': captures, 'captured_requests': sum(item['count'] for item in captures),
        'incomplete_response_bodies': incomplete,
        'consumed_response_bodies_including_incomplete': sum(item['count'] for item in captures) + len(incomplete),
        'cache_probe': cache.name, 'cache_probe_sha256': digest(cache),
        'cleanup': f'cleanup-{args.suffix}.json',
        'scope_note': 'Frozen manifest images only. Expected error statuses are retained and asserted, '
            'including deliberate callback failures. These captures do not cover later host/codec changes '
            'and do not certify universal parity. Servers CPUs8-11 and SQL helpers12-15; Windows HTTP '
            'clients/shared Docker/I/O were not isolated. No transport exceptions occurred. Raw response '
            'and body captures remain under the ignored runtime directory.'}
    (root / f'verification-{args.suffix}.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({key: result[key] for key in ['assertions', 'passed', 'failed', 'captured_requests']}))
    for item in captures:
        print(item['suite'], item['app'], item['count'])


if __name__ == '__main__':
    main()
