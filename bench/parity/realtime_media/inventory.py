"""Emit machine-readable route/channel/job/source inventory for this audit."""
import hashlib
import json
import pathlib
import re
from probe import HERE

ROOT = HERE.parents[2]
routes = json.loads((HERE.parent / 'rails-routes.json').read_text())
routes = [r for r in routes if r['path'] == '/cable' or (r.get('controller') or '').startswith(('active_storage/', 'users/push_subscriptions', 'accounts/logos', 'users/avatars', 'unfurl_links'))]
def records(files):
    return [{'path': str(file.relative_to(ROOT)).replace('\\', '/'), 'lines': len(file.read_text(encoding='utf-8').splitlines()), 'sha256': hashlib.sha256(file.read_bytes()).hexdigest(),
             'methods': [{'name': m[1], 'line': file.read_text(encoding='utf-8')[:m.start()].count('\n') + 1} for m in re.finditer(r'^\s*def\s+([^\s(]+)', file.read_text(encoding='utf-8'), re.M)]} for file in sorted(files)]
inventory = {'rails_routes': routes,
             'rails_channels': records((ROOT / 'upstream/app/channels').rglob('*.rb')),
             'rails_jobs': records((ROOT / 'upstream/app/jobs').rglob('*.rb')),
             'active_storage_jobs': records((HERE / 'runtime/framework/activestorage/app/jobs').rglob('*.rb')),
             'production_sources': records(file for feature in ('Realtime', 'Storage', 'Integrations') for file in (ROOT / ('src/Campfire/Features/' + feature)).rglob('*.cs'))}
(HERE / 'inventory.json').write_text(json.dumps(inventory, indent=2) + '\n', encoding='utf-8')
print('routes', len(routes), 'channels', len(inventory['rails_channels']), 'application jobs', len(inventory['rails_jobs']), 'framework jobs', len(inventory['active_storage_jobs']))
