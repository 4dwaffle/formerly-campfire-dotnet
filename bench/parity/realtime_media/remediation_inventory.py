"""Current owned source hashes and initial remediation evidence counts.

Does not modify the baseline report or its inventories. No app/network traffic.
"""
import datetime
import hashlib
import json
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parents[3]
OUTPUT = ROOT / 'bench/parity/remediation/realtime_media'
BASELINE = ROOT / 'bench/parity/realtime_media'


def main():
    original = json.loads((BASELINE / 'inventory.json').read_text(encoding='utf-8'))
    sources = []
    for feature in ('Realtime', 'Storage', 'Integrations'):
        for path in sorted((ROOT / 'src/Campfire/Features' / feature).rglob('*')):
            if path.suffix not in ('.cs', '.c'):
                continue
            text = path.read_text(encoding='utf-8')
            sources.append({'path': path.relative_to(ROOT).as_posix(), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
                            'lines': len(text.splitlines()), 'methods': [{'name': match[1], 'line': text[:match.start()].count('\n') + 1}
                            for match in re.finditer(r'^\s*(?:public|private|protected|internal)\s+(?:static\s+)?(?:async\s+)?[^\n=;]+?\s+(\w+)\(', text, re.M)]})
    captures = []
    for path in sorted((OUTPUT / 'runtime').glob('*/results.json')):
        data = json.loads(path.read_text(encoding='utf-8'))
        captures.append({'path': path.relative_to(ROOT).as_posix(), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
                         'http_responses': len(data.get('http', [])), 'cable_packets': len(data.get('cable_packets', [])),
                         'non_success': [{'label': row['label'], 'status': row['status']} for row in data.get('http', []) if row['status'] >= 400]})
    result = {'generated_utc': datetime.datetime.now(datetime.UTC).isoformat(), 'rails_application_revision': '6c7f8fa15f8c39478af64a483dcdf6223527ed22',
              'sources': sources, 'initial_captures': captures, 'baseline_rails_routes': original['rails_routes'],
              'baseline_rails_channels': original['rails_channels'], 'baseline_rails_jobs': original['rails_jobs'],
              'baseline_framework_jobs': original['active_storage_jobs']}
    (OUTPUT / 'inventory.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print('sources', len(sources), 'initial HTTP responses', sum(row['http_responses'] for row in captures),
          'initial channel packets', sum(row['cable_packets'] for row in captures))


if __name__ == '__main__':
    main()
