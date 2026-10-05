import json
from probe import HERE

for name in ('rails', 'dotnet', 'aot'):
    data = json.loads((HERE / 'runtime' / name / 'results.json').read_text())
    media = json.loads((HERE / 'runtime' / (name + '-media') / 'results.json').read_text())
    print(name, 'HTTP', [(x['label'], x['status']) for x in data['http']])
    print('WS', {key: data.get(key) for key in ('normal', 'no_protocol', 'wrong_origin_scheme', 'presence_absent', 'presence_present', 'presence_expired_unsubscribe')})
    print('MEDIA', {key: media.get(key) for key in ('post_status', 'before_thumbnail_fetch', 'after_thumbnail_fetch', 'thumbnail', 'error')})
    print('MEDIA_HTTP', [(x['label'], x['status']) for x in media['http']])
