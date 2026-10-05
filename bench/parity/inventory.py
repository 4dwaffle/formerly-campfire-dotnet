"""Export structural route coverage; route presence is not behavioral parity."""
import json
import pathlib
import re
HERE = pathlib.Path(__file__).resolve().parent

def canonical(path):
    path = path.replace('(.:format)', '')
    path = re.sub(r'\{\*\*[^}]+\}', '{*}', path)
    path = re.sub(r'\{[^}]+\}', lambda m: '{*}' if m[0]=='{*}' else '{}', path)
    path = re.sub(r':[a-z_]+', '{}', path)
    path = re.sub(r'\*[a-z_]+', '{*}', path)
    return path

def main():
    rails = json.loads((HERE/'rails-routes.json').read_text())
    dotnet = json.loads((HERE/'dotnet-routes.json').read_text())
    endpoints = {(verb,canonical(r['path'])) for r in dotnet for verb in r['methods']}
    rows = []
    for r in rails:
        item = dict(r)
        item['candidate_mapped'] = (r['verb'],canonical(r['path'])) in endpoints
        item['classification'] = 'framework' if (r['controller'] or '').startswith(('rails/','active_storage/','action_mailbox/','turbo/')) else 'application'
        rows.append(item)
    (HERE/'route-coverage.json').write_text(json.dumps(rows,indent=2)+'\n')
    lines = ['# Route inventory', '', 'Exported from the running pinned Rails application and ASP.NET EndpointDataSource. Parameter names and optional format suffixes are normalized for candidate matching. A mapped candidate does not prove response, authorization, format, or side-effect compatibility. See the feature reports.', '', '| Method | Rails route | Controller/action | Action exists | .NET candidate |', '|---|---|---|---|---|']
    for r in rows:
        lines.append(f"| {r['verb'] or 'mount'} | `{r['path']}` | {r['controller']}/{r['action']} | {r['implemented_action']} | {r['candidate_mapped']} |")
    (HERE/'route-coverage.md').write_text('\n'.join(lines)+'\n')
    print(json.dumps({'rails_routes':len(rows),'implemented_actions':sum(r['implemented_action'] is True for r in rows),'dotnet_endpoints':len(dotnet),'dotnet_method_routes':len(endpoints),'implemented_missing_candidates':[(r['verb'],r['path']) for r in rows if r['implemented_action'] and not r['candidate_mapped']]},indent=2))

if __name__ == '__main__': main()
