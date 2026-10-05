"""Compare served files to the pinned Rails image's actual compiled bytes."""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--instances', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    instances = json.loads(args.instances.read_text())
    container = instances['instances']['rails']['container']
    ruby = "m=JSON.parse(File.read('/rails/public/assets/.manifest.json')); puts JSON.generate(m.transform_values { |v| {path: v['digested_path'],sha256: Digest::SHA256.file('/rails/public/assets/'+v['digested_path']).hexdigest} })"
    original = json.loads(subprocess.check_output(['docker', 'exec', container, 'ruby', '-rjson', '-rdigest', '-e', ruby], text=True, encoding='utf-8'))
    current = json.loads((ROOT / 'src/Campfire/wwwroot/manifest.json').read_text())
    checks = []
    for logical, value in original.items():
        entry = current.get(logical)
        path = ROOT / 'src/Campfire/wwwroot/assets' / (entry['digested_path'] if entry else 'missing')
        digest = hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None
        checks.append({'logical': logical, 'expected_path': value['path'], 'current_path': entry['digested_path'] if entry else None, 'expected_sha256': value['sha256'], 'actual_sha256': digest, 'passed': value['sha256'] == digest})
    result = {'rails_image': instances['instances']['rails']['image'], 'assets': len(checks), 'passed': sum(c['passed'] for c in checks), 'failed': sum(not c['passed'] for c in checks), 'checks': checks}
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in result.items() if k != 'checks'}))
    raise SystemExit(1 if result['failed'] else 0)


if __name__ == '__main__':
    main()
