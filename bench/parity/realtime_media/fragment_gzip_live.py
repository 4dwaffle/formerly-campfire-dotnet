"""Actual deployed gzip/ETag/cache checks; local disposable fixtures only.

Never times throughput, builds images or edits production. Raw responses remain
under the ignored fixture directory; portable evidence excludes cookies/tokens.
"""
import argparse
import datetime
import gzip
import hashlib
import html
import http.cookiejar
import json
import pathlib
import re
import struct
import subprocess
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zlib

ROOT = pathlib.Path(__file__).resolve().parents[3]


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl): return None


class Client:
    def __init__(self, instance, output):
        self.info = instance
        self.base = instance['base']
        self.output = output
        output.mkdir(parents=True, exist_ok=True)
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect(), urllib.request.HTTPCookieProcessor(self.jar))
        self.requests = []
        self.csrf = ''

    def request(self, label, method, path, fields=None, headers=None):
        url = urllib.parse.urljoin(self.base, path)
        assert urllib.parse.urlsplit(url).netloc == urllib.parse.urlsplit(self.base).netloc
        request_headers = {'Accept-Encoding':'identity', **(headers or {})}
        data = None
        if fields is not None:
            data = urllib.parse.urlencode(fields).encode()
            request_headers['Content-Type'] = 'application/x-www-form-urlencoded'
        req = urllib.request.Request(url, data=data, method=method, headers=request_headers)
        try: response = self.opener.open(req, timeout=20)
        except urllib.error.HTTPError as error: response = error
        raw = response.read()
        header = {key.lower():value for key,value in response.headers.items() if key.lower() != 'set-cookie'}
        body = gzip.decompress(raw) if header.get('content-encoding') == 'gzip' and raw else raw
        if header.get('content-encoding') == 'gzip' and raw:
            assert zlib.decompress(raw,31) == body
            crc,length = struct.unpack('<II', raw[-8:])
            assert crc == zlib.crc32(body) and length == len(body)&0xffffffff
        record = {'label':label,'method':method,'path':path,'status':response.code,
                  'request_accept_encoding':req.get_header('Accept-encoding'),
                  'headers':header,'wire_bytes':len(raw),'decoded_bytes':len(body),
                  'wire_sha256':hashlib.sha256(raw).hexdigest(),'decoded_sha256':hashlib.sha256(body).hexdigest()}
        self.requests.append(record)
        (self.output/(label+'.body')).write_bytes(raw)
        if body != raw: (self.output/(label+'.decoded.body')).write_bytes(body)
        return record,body

    def login(self, labels, email_key='emails.david'):
        sample,body = self.request('login-form','GET','/session/new')
        assert sample['status'] == 200
        token = re.search(rb'name="authenticity_token"[^>]*value="([^"]+)"',body)
        assert token
        self.csrf = html.unescape(token[1].decode())
        sample,_ = self.request('login','POST','/session',{'email_address':labels[email_key],
            'password':labels['passwords.all'],'authenticity_token':self.csrf})
        assert sample['status'] in (302,303),sample

    def sql(self, sql):
        result = subprocess.run(['docker','run','--rm','--cpuset-cpus','28-31','--entrypoint','sqlite3','-v',
            self.info['volume']+':/data','campfire-parity-dotnet','-json','/data/db/production.sqlite3',sql],
            capture_output=True,text=True,check=True)
        return json.loads(result.stdout or '[]')

    def save(self): (self.output/'requests.json').write_text(json.dumps(self.requests,indent=2)+'\n')


def etag_body(sample, body):
    assert sample['status'] == 200,sample
    assert sample['headers'].get('etag') == 'W/"'+hashlib.sha256(body).hexdigest()[:32]+'"',sample
    assert sample['headers'].get('content-type','').startswith('text/html'),sample


def csrf(body):
    match = re.search(rb'<meta[^>]*name="csrf-token"[^>]*content="([^"]+)"',body)
    assert match
    return html.unescape(match[1].decode())


def message_ids(body):
    return re.findall(rb'<div\b[^>]*\bid="message_([^"$]+)"',body)


def message_fragment(body, marker):
    start = body.index(('id="message_'+marker+'"').encode())
    start = body.rfind(b'<div',0,start)
    depth = 0
    for tag in re.finditer(rb'</?div\b[^>]*>',body[start:]):
        depth += -1 if tag.group().startswith(b'</') else 1
        if depth == 0: return body[start:start+tag.end()]
    raise AssertionError('message root is unclosed')


def check_implementation(name, info, labels, output, literal_conditional=False, require_assembly_plan=False):
    client = Client(info,output/name)
    observations = []
    result = {'implementation':name,'image':info['image'],'image_id':info['image_id'],'controls':observations}
    try:
        client.login(labels)
        room = str(labels['rooms.watercooler'])
        path = '/rooms/'+room
        identity,plain = client.request('room-identity','GET',path)
        etag_body(identity,plain)
        assert 'content-encoding' not in identity['headers']
        original_ids = message_ids(plain)
        assert len(original_ids) == 40,(len(original_ids),identity)
        client.csrf = csrf(plain)
        observations.append('identity full UTF8 body / SHA256-derived Rails ETag / 40 message IDs')

        first,decoded = client.request('room-gzip-cold','GET',path,headers={'Accept-Encoding':'gzip'})
        etag_body(first,decoded)
        assert first['headers'].get('content-encoding') == 'gzip',first
        assert message_ids(decoded) == original_ids
        if name != 'rails': assert int(first['headers']['content-length']) == first['wire_bytes']
        assert 'accept-encoding' in first['headers'].get('vary','').lower()
        observations.append('actual gzip independently decoded with CRC/ISIZE, whole-body ETag and same message IDs')

        second,warm = client.request('room-gzip-warm','GET',path,headers={'Accept-Encoding':'gzip'})
        etag_body(second,warm)
        assert second['headers'].get('content-encoding') == 'gzip'
        assert message_ids(warm) == original_ids
        assert csrf(decoded) != csrf(warm) and first['headers']['etag'] != second['headers']['etag']
        observations.append('fresh request CSRF and fresh whole-body validator during warm fragment reuse')
        result['gzip_sample'] = {'wire_bytes':second['wire_bytes'],'decoded_bytes':second['decoded_bytes'],
            'wire_sha256':second['wire_sha256'],'decoded_sha256':second['decoded_sha256'],'etag':second['headers']['etag']}

        stale,newbody = client.request('fresh-token-old-etag','GET',path,headers={'Accept-Encoding':'gzip','If-None-Match':second['headers']['etag']})
        etag_body(stale,newbody)
        assert stale['headers']['etag'] != second['headers']['etag']
        observations.append('previous-token ETag correctly yields new 200 body rather than erroneous 304')

        sample,body = client.request('wildcard-304','GET',path,headers={'Accept-Encoding':'gzip','If-None-Match':'*'})
        if name == 'rails' or literal_conditional:
            etag_body(sample,body)
            result['wildcard_dynamic_room_status'] = sample['status']
            observations.append('Rack literal dynamic room wildcard If-None-Match returns 200 with a fresh full body')
        else:
            assert sample['status'] == 304 and body == b'' and 'content-encoding' not in sample['headers'],sample
            result['wildcard_dynamic_room_status'] = sample['status']
            observations.append('port dynamic room wildcard 304 has empty body and no gzip encoding; differs from pinned Rails')

        sample,body = client.request('head-gzip','HEAD',path,headers={'Accept-Encoding':'gzip'})
        assert sample['status'] == 200 and body == b'',sample
        assert sample['headers'].get('content-length') in ('0',None)
        result['head_gzip_content_encoding'] = sample['headers'].get('content-encoding')
        observations.append('HEAD gzip negotiation preserves empty response without gzip payload')

        sample,body = client.request('head-304','HEAD',path,headers={'Accept-Encoding':'gzip','If-None-Match':'*'})
        assert sample['status'] == (200 if name == 'rails' or literal_conditional else 304) and body == b'',sample
        if sample['status']==304: assert 'content-encoding' not in sample['headers']
        observations.append('conditional HEAD preserves empty body and expected literal/wildcard status')

        negotiations = [('gzip-zero','gzip;q=0',False),('identity-preferred','identity;q=1,gzip;q=0.5',name=='rails'),
                        ('unsupported','deflate',False),('gzip-preferred','gzip;q=1,identity;q=0.1',True),
                        ('gzip-exclude-br','br;q=0,gzip;q=1',True),('invalid-q','gzip;q=invalid',name=='rails')]
        for label,accept,compressed in negotiations:
            sample,body = client.request(label,'GET',path,headers={'Accept-Encoding':accept})
            etag_body(sample,body)
            assert (sample['headers'].get('content-encoding') == 'gzip') == compressed,sample
            assert message_ids(body) == original_ids
        observations.append('six Accept-Encoding preference/exclusion/invalid/unsupported cases preserve correct body')

        if literal_conditional:
            # Stable JSON has no fresh CSRF bytes, unlike a rendered room. It
            # exercises real exact/list/weak matching, rather than a trivially
            # different validator on each dynamic page request.
            stable_path = '/webmanifest.json'
            json_headers = {'Accept':'application/json'}
            stable,stable_body = client.request('stable-json','GET',stable_path,headers=json_headers)
            assert stable['status']==200 and stable['headers'].get('content-type','').startswith('application/json'),stable
            json.loads(stable_body)
            validator = stable['headers']['etag']
            repeat,repeat_body = client.request('stable-json-repeat','GET',stable_path,headers=json_headers)
            assert repeat_body==stable_body and repeat['headers']['etag']==validator
            sample,body = client.request('stable-json-exact-304','GET',stable_path,headers={**json_headers,'If-None-Match':validator,'Accept-Encoding':'gzip'})
            assert sample['status']==304 and body==b'' and 'content-encoding' not in sample['headers'],sample
            observations.append('stable JSON exact original weak ETag yields empty304 without gzip')
            for label,candidate in [('weak-equivalent',validator.removeprefix('W/')),('list',validator+', "different"'),('wildcard','*')]:
                sample,body = client.request('stable-json-'+label,'GET',stable_path,headers={**json_headers,'If-None-Match':candidate})
                assert sample['status']==200 and body==stable_body,sample
            observations.append('stable JSON weak-equivalent/list/wildcard validators remain200 under literal Rack matching')
            sample,body = client.request('stable-json-head-exact','HEAD',stable_path,headers={**json_headers,'If-None-Match':validator,'Accept-Encoding':'gzip'})
            assert sample['status']==304 and body==b'' and 'content-encoding' not in sample['headers'],sample
            observations.append('stable JSON conditional HEAD exact ETag yields empty304 without gzip')

        # Verify another logged-in viewer receives their own shell/token, while
        # reusing immutable shared message fragments. No cookie/token is reported.
        other = Client(info,output/(name+'-other-viewer'))
        try:
            other.login(labels,'emails.jason')
            sample,otherbody = other.request('room-gzip','GET',path,headers={'Accept-Encoding':'gzip'})
            etag_body(sample,otherbody)
            assert csrf(otherbody) != csrf(warm)
            assert message_ids(otherbody) == original_ids
            sample,_ = other.request('reject-other-session-token','POST','/rooms/'+room+'/messages',
                {'message[body]':'must never persist','authenticity_token':client.csrf},
                {'Accept':'text/vnd.turbo-stream.html','X-CSRF-Token':client.csrf})
            assert sample['status'] == 422,sample
            observations.append('shared immutable message fragments retain separate session CSRF; foreign-session mutation rejected')
        finally: other.save(); client.requests.extend(other.requests)

        marker = 'codec-'+uuid.uuid4().hex
        fresh,body = client.request('pre-create-room','GET',path,headers={'Accept-Encoding':'gzip'})
        etag_body(fresh,body)
        client.csrf = csrf(body)
        sample,_ = client.request('create','POST','/rooms/'+room+'/messages',
            {'message[body]':'<p>codec initial 雪 😀</p>','message[client_message_id]':marker,'authenticity_token':client.csrf},
            {'Accept':'text/vnd.turbo-stream.html','X-CSRF-Token':client.csrf})
        assert sample['status'] == 200,sample
        sample,created = client.request('created-room','GET',path,headers={'Accept-Encoding':'gzip'})
        etag_body(sample,created)
        assert 'codec initial 雪 😀'.encode() in message_fragment(created,marker)
        observations.append('created message enters compressed page with exact whole-body validator')
        rows = client.sql("SELECT id FROM messages WHERE client_message_id='"+marker+"'")
        assert len(rows) == 1
        message_id = rows[0]['id']
        sample,_ = client.request('update','PATCH','/messages/'+str(message_id)+'?room_id='+room,
            {'message[body]':'<p>codec updated ☃ 🔥</p>','authenticity_token':client.csrf},
            {'Accept':'text/html','X-CSRF-Token':client.csrf})
        assert sample['status'] in (302,303),sample
        sample,updated = client.request('updated-room','GET',path,headers={'Accept-Encoding':'gzip'})
        etag_body(sample,updated)
        updated_fragment = message_fragment(updated,marker)
        assert 'codec updated ☃ 🔥'.encode() in updated_fragment and 'codec initial 雪 😀'.encode() not in updated_fragment
        observations.append('touched immutable message version invalidates compressed fragment without stale text')

        sample,_ = client.request('delete','DELETE','/messages/'+str(message_id)+'?room_id='+room,
            headers={'Accept':'text/vnd.turbo-stream.html','X-CSRF-Token':client.csrf})
        assert sample['status'] == 200,sample
        sample,deleted = client.request('deleted-room','GET',path,headers={'Accept-Encoding':'gzip'})
        etag_body(sample,deleted)
        assert marker.encode() not in deleted and message_ids(deleted) == original_ids
        observations.append('deleted message disappears and original page message IDs return')

        logs = subprocess.run(['docker','logs',info['container']],capture_output=True,text=True,check=True)
        log = logs.stdout+logs.stderr
        (output/(name+'-live-server.log')).write_text(log)
        if name != 'rails':
            assert log.count('Campfire fragment gzip: native zlib') == 1,log[-4000:]
            assert log.count('Campfire fragment gzip: cache reuse active') == 1,log[-4000:]
            observations.append('actual deployed native helper and immutable cache reuse confirmed by one-time backend logs')
            result['native_backend_marker_count'] = log.count('Campfire fragment gzip: native zlib')
            result['cache_backend_marker_count'] = log.count('Campfire fragment gzip: cache reuse active')
            if require_assembly_plan:
                assert log.count('Campfire fragment gzip: assembly plan reuse active') == 1,log[-4000:]
                result['assembly_plan_marker_count'] = log.count('Campfire fragment gzip: assembly plan reuse active')
        result['passed'] = True
    except Exception as error:
        result['passed'] = False
        result['error'] = repr(error)
    finally:
        result['http_requests'] = len(client.requests)
        result['expected_rejection_statuses'] = [record['status'] for record in client.requests if record['label']=='reject-other-session-token']
        result['http_server_error_count'] = sum(record['status']>=500 for record in client.requests)
        client.save()
    print(name,result['passed'],result.get('error',''),flush=True)
    return result


def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--root',type=pathlib.Path,required=True); parser.add_argument('--portable',type=pathlib.Path,required=True); parser.add_argument('--source-hash',required=True); parser.add_argument('--rack-literal-conditional',action='store_true'); parser.add_argument('--require-assembly-plan',action='store_true'); parser.add_argument('--build-manifest'); args=parser.parse_args()
    document=json.loads((args.root/'instances.json').read_text())
    files=sorted(path for path in (ROOT/'src/Campfire').rglob('*') if path.is_file() and path.suffix.lower()!='.md' and not {'bin','obj'}.intersection(path.parts))
    source_hash=hashlib.sha256(b''.join(path.relative_to(ROOT).as_posix().encode()+b'\0'+path.read_bytes() for path in files)).hexdigest()
    # The image remains immutable when the parent starts followup source fixes.
    # Attribute its hash to the saved build manifest, while recording the live
    # workspace hash separately instead of falsely conflating the two.
    records=[check_implementation(name,info,document['labels'],args.root/'checks-v7',args.rack_literal_conditional,args.require_assembly_plan) for name,info in document['instances'].items()]
    proof={'verified_utc':datetime.datetime.now(datetime.UTC).isoformat(),'application_runtime_source_sha256':args.source_hash,
        'image_source_attribution':('Parent frozen '+args.build_manifest+'; image IDs checked from Docker.' if args.build_manifest else ('Parent frozen rust-inspired-final-build.json; image IDs checked from Docker.' if args.rack_literal_conditional else 'Parent frozen rust-inspired-initial-build.json; image IDs checked from Docker.')),
        'observed_workspace_runtime_source_sha256':source_hash,
        'source_files':len(files),'server_cpus':document['server_cpus'],'fixture':str(args.root).replace('\\','/'),
        'records':records,'http_requests':sum(record['http_requests'] for record in records),
        'server_error_count':sum(record['http_server_error_count'] for record in records),'controls_passed':sum(len(record['controls']) for record in records),
        'all_passed':all(record['passed'] for record in records),'timing_benchmark':False,'cleanup_verified':False,
        'rack_literal_conditional':args.rack_literal_conditional,
        'assembly_plan_required':args.require_assembly_plan,
        'confirmed_differences':([] if args.rack_literal_conditional else ['Dynamic room If-None-Match wildcard: pinned Rails200; frozen port304 (also conditional HEAD).'])+['Accept-Encoding identity;q=1,gzip;q=0.5: pinned Rails front-Thrustgzip but directRackidentity; frozen portidentity.','Accept-Encoding gzip;q=invalid: pinned directRack/front-Thrustgzip; frozen portidentity.'],
        'unverified':['Response Cache-Control:no-transform integration: no disposable route emits it.',('This functional run has no timing; parent same-image pilot/full benchmarks measure throughput separately.' if args.require_assembly_plan else 'Throughput improvement: parent initial controlled run shows regression, not a win.')]}
    args.portable.write_text(json.dumps(proof,indent=2)+'\n')
    assert proof['all_passed'],records


if __name__=='__main__':main()
