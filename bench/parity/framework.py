"""Read-only differential probes against disposable parity instances."""
import base64
import argparse
import hashlib
import json
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE / 'identity'))
from check import Client

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest',type=pathlib.Path,default=HERE/'browser/runtime/instances.json')
    parser.add_argument('--output',type=pathlib.Path,default=HERE/'browser/results')
    args=parser.parse_args()
    manifest = json.loads(args.manifest.read_text())
    output = args.output
    output.mkdir(parents=True, exist_ok=True)
    records = {}
    qr = base64.urlsafe_b64encode(b'https://example.com/join/fixture').decode()
    cases = [
        ('health','/up','GET',{}), ('health-head','/up','HEAD',{}),
        ('login-head','/session/new','HEAD',{}), ('login-options','/session/new','OPTIONS',{}),
        ('login-html','/session/new.html','GET',{}),
        ('old-browser','/session/new?parity_browser=119','GET',{'User-Agent':'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36'}),
        ('manifest','/webmanifest','GET',{}), ('manifest-head','/webmanifest','HEAD',{}),
        ('manifest-json','/webmanifest.json','GET',{}),
        ('manifest-json-accept','/webmanifest','GET',{'Accept':'application/json'}),
        ('worker','/service-worker','GET',{}), ('worker-head','/service-worker','HEAD',{}),
        ('worker-js','/service-worker','GET',{'Accept':'text/javascript'}),
        ('qr','/qr_code/'+qr,'GET',{}), ('qr-invalid','/qr_code/%%%','GET',{}),
        ('qr-svg-format','/qr_code/'+qr+'.svg','GET',{}), ('qr-whitespace','/qr_code/'+qr+'%20','GET',{}),
        ('qr-binary','/qr_code/_w==','GET',{}),
        ('robots','/robots.txt','GET',{}), ('unknown','/definitely-missing','GET',{}),
        ('unknown-json','/definitely-missing.json','GET',{}), ('unknown-head','/definitely-missing','HEAD',{}),
        ('error-404','/404.html','GET',{}), ('error-422','/422.html','GET',{}),
        ('error-500','/500.html','GET',{}), ('error-502','/502.html','GET',{}),
        ('native-recede','/recede_historical_location','GET',{}),
        ('native-refresh','/refresh_historical_location','GET',{}),
        ('native-resume','/resume_historical_location','GET',{}),
        ('mailbox-postmark','/rails/action_mailbox/postmark/inbound_emails','POST',{}),
        ('mailbox-relay','/rails/action_mailbox/relay/inbound_emails','POST',{}),
        ('mailbox-sendgrid','/rails/action_mailbox/sendgrid/inbound_emails','POST',{}),
        ('mailbox-mandrill','/rails/action_mailbox/mandrill/inbound_emails','GET',{}),
        ('mailbox-mailgun','/rails/action_mailbox/mailgun/inbound_emails/mime','POST',{}),
        ('mailbox-conductor','/rails/conductor/action_mailbox/inbound_emails','GET',{})]
    for name, instance in manifest['instances'].items():
        client = Client(instance)
        for case,path,method,headers in cases:
            client.request(case,path,method,headers=headers)
        client.login(manifest['labels'],'david')
        client.request('room-head','/rooms/'+str(manifest['labels']['rooms.watercooler']), 'HEAD') if 'rooms.watercooler' in manifest['labels'] else client.request('root-head','/', 'HEAD')
        records[name] = client.records
        (output/(name+'.json')).write_text(json.dumps(client.records,indent=2)+'\n')
    rows = []
    maps = {n:{r['case']:r for r in rr} for n,rr in records.items()}
    for case,*_ in cases:
        row = {'case':case,'results':{n:{k:maps[n][case][k] for k in ('status','content_type','body_sha256')} for n in maps}}
        row['status_type_match'] = all((maps[n][case]['status'],maps[n][case]['content_type']) == (maps['rails'][case]['status'],maps['rails'][case]['content_type']) for n in ('dotnet','aot'))
        rows.append(row)
    (output/'comparison.json').write_text(json.dumps(rows,indent=2)+'\n')
    print('\n'.join(f"{r['case']}: " + ', '.join(f"{n}={v['status']}/{v['content_type']}" for n,v in r['results'].items()) for r in rows))
    differences=sum(not r['status_type_match'] for r in rows)
    print(f'{differences}/{len(rows)} named probes differ on status or MIME; matching probes still need body/side-effect review.')
    return 1 if differences else 0

if __name__ == '__main__':
    raise SystemExit(main())
