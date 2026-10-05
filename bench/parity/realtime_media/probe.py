"""Read/write functional parity probes against this family's disposable seed clones.

No external HTTP requests: the only network connections are the three supplied
loopback servers. Push registration performs server-side DNS validation, then the
new subscription is removed before any message is created. No push is delivered.
Raw response bodies, headers (except cookies), WS packets and DB snapshots are kept.
"""
import base64
import hashlib
import html
import http.cookiejar
import json
import os
import pathlib
import re
import secrets
import socket
import struct
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request

HERE = pathlib.Path(os.environ.get('CAMPFIRE_PARITY_ROOT', pathlib.Path(__file__).resolve().parent))
INSTANCES = json.loads((HERE / 'runtime/instances.json').read_text())
LABELS = INSTANCES['labels']


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Client:
    def __init__(self, name, info):
        self.name, self.info, self.base = name, info, info['base']
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(NoRedirect(), urllib.request.HTTPCookieProcessor(self.jar))
        self.output = HERE / 'runtime' / name
        self.output.mkdir(exist_ok=True)
        self.samples = []

    def request(self, label, method, path, data=None, headers=None):
        url = urllib.parse.urljoin(self.base, path)
        if urllib.parse.urlsplit(url).netloc != urllib.parse.urlsplit(self.base).netloc:
            raise ValueError('External HTTP request rejected')
        req = urllib.request.Request(url, data=data, method=method, headers=headers or {})
        start = time.monotonic()
        try:
            response = self.opener.open(req, timeout=15)
        except urllib.error.HTTPError as error:
            response = error
        body = response.read()
        sample = dict(label=label, method=method, url=url, status=response.code,
                      headers={k: v for k, v in response.headers.items() if k.lower() != 'set-cookie'},
                      bytes=len(body), sha256=hashlib.sha256(body).hexdigest(),
                      elapsed_ms=(time.monotonic() - start) * 1000)
        self.samples.append(sample)
        (self.output / (label + '.body')).write_bytes(body)
        return sample, body

    def form(self, label, path, values):
        return self.request(label, 'POST', path, urllib.parse.urlencode(values).encode(),
                            {'Content-Type': 'application/x-www-form-urlencoded', 'X-CSRF-Token': self.csrf})

    def login(self):
        _, body = self.request('login_form', 'GET', '/session/new')
        token = re.search(rb'name="authenticity_token"[^>]*value="([^"]+)"', body)
        self.csrf = html.unescape(token[1].decode())
        sample, _ = self.form('login', '/session', {'email_address': LABELS['emails.david'],
                                                   'password': LABELS['passwords.all'], 'authenticity_token': self.csrf})
        if sample['status'] not in (302, 303):
            raise ValueError('Login failed: ' + str(sample))
        _, body = self.request('room', 'GET', '/rooms/' + str(LABELS['rooms.hq']))
        token = re.search(rb'<meta[^>]*name="csrf-token"[^>]*content="([^"]+)"', body)
        self.csrf = html.unescape(token[1].decode())
        self.room_html = body.decode()

    def sql(self, query):
        result = subprocess.run(['docker', 'run', '--rm', '--entrypoint', 'sqlite3', '-v',
                                 self.info['volume'] + ':/data', 'campfire-parity-dotnet', '-json',
                                 '/data/db/production.sqlite3', query], check=True, capture_output=True, text=True)
        return json.loads(result.stdout or '[]')

    def finish(self, values):
        (self.output / 'results.json').write_text(json.dumps({'http': self.samples, **values}, indent=2) + '\n')


class Cable:
    def __init__(self, client, protocol='actioncable-v1-json', origin=None):
        parsed = urllib.parse.urlsplit(client.base)
        self.socket = socket.create_connection((parsed.hostname, parsed.port), timeout=5)
        self.socket.settimeout(5)
        self.packets = []
        self.buffer = b''
        key = base64.b64encode(secrets.token_bytes(16)).decode()
        cookie = '; '.join(c.name + '=' + c.value for c in client.jar)
        headers = ['GET /cable HTTP/1.1', 'Host: ' + parsed.netloc, 'Upgrade: websocket',
                   'Connection: Upgrade', 'Sec-WebSocket-Version: 13', 'Sec-WebSocket-Key: ' + key,
                   'Cookie: ' + cookie, 'Origin: ' + (origin or client.base)]
        if protocol:
            headers += ['Sec-WebSocket-Protocol: ' + protocol]
        self.socket.sendall(('\r\n'.join(headers) + '\r\n\r\n').encode())
        while b'\r\n\r\n' not in self.buffer:
            chunk = self.socket.recv(8192)
            if not chunk:
                break
            self.buffer += chunk
        header, _, self.buffer = self.buffer.partition(b'\r\n\r\n')
        self.handshake = '\r\n'.join(line for line in header.decode(errors='replace').split('\r\n') if not line.lower().startswith('set-cookie:'))

    def read(self, size):
        while len(self.buffer) < size:
            chunk = self.socket.recv(max(8192, size - len(self.buffer)))
            if not chunk:
                raise EOFError('socket closed')
            self.buffer += chunk
        result, self.buffer = self.buffer[:size], self.buffer[size:]
        return result

    def packet(self):
        first, second = self.read(2)
        size = second & 127
        if size == 126:
            size = struct.unpack('>H', self.read(2))[0]
        elif size == 127:
            size = struct.unpack('>Q', self.read(8))[0]
        payload = self.read(size)
        if first & 15 == 8:
            value = {'close': payload.hex()}
        elif first & 15 == 1:
            value = json.loads(payload)
        else:
            value = {'opcode': first & 15, 'data': payload.hex()}
        self.packets.append(value)
        return value

    def send(self, command, identifier, action=None):
        value = {'command': command, 'identifier': json.dumps(identifier, separators=(',', ':'))}
        if action is not None:
            value['data'] = json.dumps({'action': action})
        self.send_value(value)

    def send_value(self, value):
        payload = json.dumps(value).encode()
        mask = secrets.token_bytes(4)
        length = bytes([len(payload) | 128]) if len(payload) < 126 else b'\xfe' + struct.pack('>H', len(payload))
        self.socket.sendall(b'\x81' + length + mask + bytes(v ^ mask[i % 4] for i, v in enumerate(payload)))

    def wait(self, kind):
        for _ in range(12):
            value = self.packet()
            if value.get('type') == kind or kind == 'message' and 'message' in value and 'identifier' in value:
                return value
        raise ValueError('expected packet ' + kind)

    def close(self):
        self.socket.close()


def probe(client):
    # Permit reruns after this probe's explicit membership revocation.
    client.sql(f"INSERT OR IGNORE INTO memberships(user_id,room_id,involvement,created_at,updated_at) VALUES({LABELS['users.david']},{LABELS['rooms.hq']},'everything',datetime('now'),datetime('now'))")
    client.login()
    findings = {}
    room = LABELS['rooms.hq']
    uid = LABELS['users.david']
    client.request('logo_head', 'HEAD', '/account/logo')
    client.request('avatar_head', 'HEAD', '/users/' + LABELS['avatar_tokens.david'] + '/avatar')
    client.request('push_missing_destroy', 'DELETE', '/users/me/push_subscriptions/999999999999', headers={'X-CSRF-Token': client.csrf})
    client.request('unfurl_blank', 'POST', '/unfurl_link', b'{"url":""}', {'Content-Type': 'application/json', 'X-CSRF-Token': client.csrf})
    # Registration only, no delivery. Use deliberately invalid keys from pinned Rails tests.
    push = {'push_subscription': {'endpoint': 'https://fcm.googleapis.com/fcm/send/parity-audit-no-delivery', 'p256dh_key': '123', 'auth_key': '456'}}
    client.request('push_dummy_keys', 'POST', '/users/me/push_subscriptions', json.dumps(push).encode(), {'Content-Type': 'application/json', 'X-CSRF-Token': client.csrf})
    findings['push_rows'] = client.sql("SELECT id,endpoint FROM push_subscriptions WHERE endpoint LIKE '%parity-audit-no-delivery'")
    for row in findings['push_rows']:
        client.request('push_cleanup', 'DELETE', '/users/me/push_subscriptions/' + str(row['id']), headers={'X-CSRF-Token': client.csrf})
    raw = b'local parity bytes\n'
    blob = {'blob': {'filename': 'parity.txt', 'content_type': 'text/plain', 'byte_size': len(raw), 'checksum': base64.b64encode(hashlib.md5(raw).digest()).decode()}}
    client.form('direct_form', '/rails/active_storage/direct_uploads', {'authenticity_token': client.csrf, **{'blob[' + key + ']': value for key, value in blob['blob'].items()}})
    sample, body = client.request('direct_json', 'POST', '/rails/active_storage/direct_uploads', json.dumps(blob).encode(), {'Content-Type': 'application/json', 'X-CSRF-Token': client.csrf})
    if sample['status'] in (200, 201):
        uploaded = json.loads(body)
        # Generated server URI's internal host may differ, use just its signed local path.
        path = urllib.parse.urlsplit(uploaded['direct_upload']['url']).path
        client.request('disk_wrong_checksum', 'PUT', path, raw.replace(b'local', b'xxxxx'), uploaded['direct_upload']['headers'])
        client.request('disk_put', 'PUT', path, raw, uploaded['direct_upload']['headers'])
        proxy = '/rails/active_storage/blobs/proxy/' + uploaded['signed_id'] + '/parity.txt'
        client.request('blob_proxy_get', 'GET', proxy)
        client.request('blob_proxy_range', 'GET', proxy, headers={'Range': 'bytes=0-4'})
        client.request('blob_proxy_head', 'HEAD', proxy)
        client.request('blob_proxy_unsatisfiable_range', 'GET', proxy, headers={'Range': 'bytes=9999-10000'})
        findings['direct_blob'] = client.sql('SELECT id,metadata FROM active_storage_blobs WHERE id=' + str(uploaded['id']))
    # HEAD and blank-input checks require no external fetch.
    for label, protocol, origin in [('normal', 'actioncable-v1-json', None), ('no_protocol', None, None),
                                     ('wrong_origin_scheme', 'actioncable-v1-json', client.base.replace('http:', 'https:'))]:
        cable = Cable(client, protocol, origin)
        result = {'handshake': cable.handshake}
        try:
            result['first'] = cable.packet()
        except (EOFError, OSError) as error:
            result['error'] = str(error)
        cable.close()
        findings[label] = result
    cable = Cable(client)
    try:
        cable.wait('welcome')
        channels = ['HeartbeatChannel', 'ReadRoomsChannel', 'UnreadRoomsChannel', 'RoomChannel', 'PresenceChannel', 'TypingNotificationsChannel']
        for channel in channels:
            identifier = {'channel': channel}
            if channel in ('RoomChannel', 'PresenceChannel', 'TypingNotificationsChannel'):
                identifier['room_id'] = room
            cable.send('subscribe', identifier)
            cable.wait('confirm_subscription')
        cable.send('subscribe', {'channel': 'RoomChannel', 'room_id': 999999999999})
        findings['outsider'] = cable.wait('reject_subscription')
        typing = {'channel': 'TypingNotificationsChannel', 'room_id': room}
        cable.send('message', typing, 'start')
        findings['typing'] = cable.wait('message')
        presence = {'channel': 'PresenceChannel', 'room_id': room}
        cable.send('message', presence, 'absent')
        time.sleep(0.2)
        findings['presence_absent'] = client.sql(f'SELECT connections,connected_at,unread_at FROM memberships WHERE user_id={uid} AND room_id={room}')
        cable.send('message', presence, 'present')
        time.sleep(0.2)
        findings['presence_present'] = client.sql(f'SELECT connections,connected_at,unread_at FROM memberships WHERE user_id={uid} AND room_id={room}')
        # Force expiry/count2, then unsubscribe: Rails resets to0; port decrements to1.
        client.sql(f"UPDATE memberships SET connections=2,connected_at=datetime('now','-70 seconds') WHERE user_id={uid} AND room_id={room}")
        cable.send('unsubscribe', presence)
        time.sleep(0.2)
        findings['presence_expired_unsubscribe'] = client.sql(f'SELECT connections,connected_at FROM memberships WHERE user_id={uid} AND room_id={room}')
        # Revoke membership while socket remains open: socket must disconnect/reject replay.
        client.sql(f'DELETE FROM memberships WHERE user_id={uid} AND room_id={room}')
        cable.send('message', typing, 'start')
        try:
            findings['revocation'] = cable.wait('disconnect')
        except (EOFError, OSError, ValueError) as error:
            findings['revocation_error'] = str(error)
    finally:
        findings['cable_packets'] = cable.packets
        cable.close()
    client.finish(findings)
    return findings


if __name__ == '__main__':
    summary = {}
    for name, info in INSTANCES['instances'].items():
        client = Client(name, info)
        try:
            summary[name] = probe(client)
        except Exception as error:
            client.finish({'error': repr(error)})
            summary[name] = {'error': repr(error)}
        print(name, json.dumps(summary[name], separators=(',', ':')), flush=True)
    (HERE / 'runtime/summary.json').write_text(json.dumps(summary, indent=2) + '\n')
