"""Disposable original Rails/JIT/AOT Cable checks against one shared Redis.

Each app gets its own sanitized seed clone. Only local app requests and Docker
exec are used; stored webhook/push destinations are deleted before startup.
This is a functional wire test, not a benchmark or shared-database deployment.
"""
import argparse
import contextlib
import datetime
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / 'bench'))
from run import Environment, DOTNET_IMAGES, MANIFEST, PARITY_IMAGE, SEED, execute

REDIS_IMAGE = 'redis@sha256:c6eabf748fc7a61dbb5a705c78bcf3d6377b1127a97d0ce965c11c44ba46896f'


class SharedRedisEnvironment(Environment):
    def __init__(self, options, output, redis):
        super().__init__(options, output)
        self.redis = redis
        self.repetition = 'mixed'

    @contextlib.contextmanager
    def application(self, name):
        image = DOTNET_IMAGES[name] if name in DOTNET_IMAGES else MANIFEST[name]['image']
        try:
            execute('docker', 'network', 'create', self.network)
            execute('docker', 'network', 'connect', '--alias', 'benchmark-shared-redis', self.network, self.redis)
            execute('docker', 'volume', 'create', self.volume)
            uid = 1654 if name in DOTNET_IMAGES else 1000
            execute('docker', 'run', '--rm', '--entrypoint', 'bash', '-v', SEED + ':/seed:ro',
                    '-v', self.volume + ':/out', PARITY_IMAGE, '-c',
                    f'cp -a /seed/. /out/ && chown -R {uid}:{uid} /out && '
                    'sqlite3 /out/db/production.sqlite3 "DELETE FROM push_subscriptions; DELETE FROM webhooks;"')
            command = ['docker', 'run', '-d', '--name', self.app, '--network', self.network,
                       '--network-alias', 'benchmark-app', '--cpuset-cpus', self.options.server_cpus,
                       '-p', f'127.0.0.1:{self.options.port}:3000', '--env-file', ROOT / 'reference-rust/parity/.env.reference',
                       '-e', 'HTTP_PORT=3000', '-e', 'TARGET_PORT=3001', '-e', 'PORT=3001',
                       '-e', 'WEB_CONCURRENCY=3', '-e', 'JOB_CONCURRENCY=3', '-e', 'RAILS_LOG_LEVEL=warn',
                       '-e', 'ASPNETCORE_ENVIRONMENT=Production', '-e', 'DOTNET_ENVIRONMENT=Production',
                       '-e', 'Logging__LogLevel__Default=Warning', '-e', 'CAMPFIRE_STORAGE=/rails/storage',
                       '-e', 'CAMPFIRE_DELIVER_INTEGRATIONS=false', '-e', 'REDIS_URL=redis://benchmark-shared-redis:6379/0',
                       '-e', 'CAMPFIRE_REQUIRE_REDIS=true', '-v', self.volume + ':/rails/storage']
            if name in DOTNET_IMAGES:
                command += ['-e', 'ASPNETCORE_URLS=http://+:3000']
            start = time.monotonic()
            execute(*command, image)
            while True:
                try:
                    with urllib.request.urlopen(self.base + '/up', timeout=1) as response:
                        if response.status == 200:
                            break
                except (OSError, urllib.error.URLError):
                    if time.monotonic() - start > 90:
                        raise RuntimeError(name + ' startup failed: ' + execute('docker', 'logs', self.app, check=False)[-8000:])
                    time.sleep(.1)
            yield (time.monotonic() - start) * 1000, image
        finally:
            logged = subprocess.run(['docker', 'logs', self.app], capture_output=True, text=True, encoding='utf-8')
            (self.output / (name + '-mixed-server.log')).write_text(logged.stdout + logged.stderr, encoding='utf-8')
            execute('docker', 'rm', '-f', self.app, check=False)
            execute('docker', 'network', 'disconnect', self.network, self.redis, check=False)
            execute('docker', 'volume', 'rm', self.volume, check=False)
            execute('docker', 'network', 'rm', self.network, check=False)


def check_wire(root, document):
    os.environ['CAMPFIRE_PARITY_ROOT'] = str(root)
    from probe import Client, Cable
    clients = {name: Client(name, info) for name, info in document['instances'].items()}
    for client in clients.values():
        client.login()
    labels = document['labels']
    room = labels['rooms.hq']
    typing = {'channel': 'TypingNotificationsChannel', 'room_id': room}
    sockets, observations = {}, []

    def connect_all():
        for name, client in clients.items():
            connection = Cable(client)
            sockets[name] = connection
            connection.wait('welcome')
            connection.send('subscribe', typing)
            connection.wait('confirm_subscription')
            signed = re.search(r'channel="RoomMessagesChannel" signed-stream-name="([^"]+)"', client.room_html)[1]
            connection.send('subscribe', {'channel': 'RoomMessagesChannel', 'signed_stream_name': signed})
            connection.wait('confirm_subscription')

    def capture_and_close(phase):
        for name, connection in sockets.items():
            (root / 'runtime' / name / (phase + '-cable.json')).write_text(
                json.dumps({'handshake': connection.handshake, 'packets': connection.packets}, indent=2) + '\n', encoding='utf-8')
            connection.close()
        sockets.clear()

    try:
        connect_all()
        for sender in clients:
            sockets[sender].send('message', typing, 'start')
            for receiver, connection in sockets.items():
                value = connection.wait('message')['message']
                assert value == {'action': 'start', 'user': {'id': labels['users.david'], 'name': 'David'}}, value
                observations.append({'phase': 'typing', 'sender': sender, 'receiver': receiver, 'passed': True})
        for sender, client in clients.items():
            marker = 'mixed-redis-' + sender + '-' + uuid.uuid4().hex
            body=urllib.parse.urlencode({'message[body]': marker,'message[client_message_id]':marker,'authenticity_token':client.csrf}).encode()
            result, body = client.request('message-' + sender,'POST','/rooms/' + str(room) + '/messages',body,
                                          {'Content-Type':'application/x-www-form-urlencoded','X-CSRF-Token':client.csrf,'Accept':'text/vnd.turbo-stream.html'})
            assert result['status'] in (200, 201, 204), result
            for receiver, connection in sockets.items():
                value = connection.wait('message')['message']
                assert isinstance(value, str) and marker in value and '<turbo-stream action="append"' in value, value
                assert 'name="authenticity_token"' not in value, 'publisher CSRF token leaked into shared broadcast'
                observations.append({'phase': 'turbo', 'sender': sender, 'receiver': receiver, 'passed': True,
                                     'sha256': hashlib.sha256(value.encode()).hexdigest(), 'bytes': len(value.encode())})
        capture_and_close('broadcasts')
        for sender, client in clients.items():
            connect_all()
            if sender == 'rails':
                # Run the actual original AR callback, not a handcrafted Redis packet.
                ruby = f"Membership.find_by!(user_id: {labels['users.david']}, room_id: {room}).destroy!"
                execute('docker', 'exec', client.info['container'], 'bundle', 'exec', 'rails', 'runner', ruby)
            else:
                result, _ = client.request('logout-' + sender, 'DELETE', '/session', headers={'X-CSRF-Token': client.csrf})
                assert result['status'] in (302, 303), result
            for receiver, connection in sockets.items():
                value = connection.wait('disconnect')
                assert value.get('reconnect') is True, value
                observations.append({'phase': 'remote_disconnect', 'sender': sender, 'receiver': receiver, 'passed': True})
            capture_and_close('disconnect-' + sender)
            if sender == 'rails':
                execute('docker', 'exec', client.info['container'], 'bundle', 'exec', 'rails', 'runner',
                        f"Membership.create!(user_id: {labels['users.david']}, room_id: {room})")
            else:
                client.login()
    finally:
        capture_and_close('remaining')
        for client in clients.values():
            client.finish({})
        (root / 'runtime/results.json').write_text(json.dumps({'checks': observations, 'passed': len(observations)}, indent=2) + '\n', encoding='utf-8')
    assert len(observations) == 27, observations
    print('PASS mixed Rails/JIT/AOT Redis: 9 typing, 9 Turbo, 9 remote disconnect controls', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=4460)
    parser.add_argument('--server-cpus', default='16-19')
    parser.add_argument('--output', type=pathlib.Path, default=ROOT / 'bench/parity/remediation/realtime_media/mixed')
    parser.add_argument('--jit-image', required=True)
    parser.add_argument('--aot-image', required=True)
    parser.add_argument('--keep', action='store_true', help='After checks keep disposable apps until runtime/stop exists.')
    args = parser.parse_args()
    DOTNET_IMAGES.update(dotnet=args.jit_image, aot=args.aot_image)
    output = args.output.resolve() / 'runtime'
    output.mkdir(parents=True, exist_ok=True)
    if (output / 'instances.json').exists():
        raise RuntimeError('Use a fresh output directory to preserve earlier evidence')
    shared = 'campfire-mixed-' + uuid.uuid4().hex[:12]
    execute('docker', 'run', '-d', '--name', shared, REDIS_IMAGE, 'redis-server', '--appendonly', 'yes')
    try:
        with contextlib.ExitStack() as stack:
            instances = {}
            for offset, name in enumerate(('rails', 'dotnet', 'aot')):
                options = argparse.Namespace(port=args.port + offset, server_cpus=args.server_cpus)
                environment = SharedRedisEnvironment(options, output, shared)
                ready, image = stack.enter_context(environment.application(name))
                instances[name] = {'base': environment.base, 'container': environment.app, 'volume': environment.volume,
                                   'network': environment.network, 'image': image,
                                   'image_id': json.loads(execute('docker', 'image', 'inspect', image))[0]['Id'], 'ready_ms': ready}
            document = {'started_utc': datetime.datetime.now(datetime.UTC).isoformat(), 'family': 'realtime_media_mixed',
                        'shared_redis': shared, 'redis_image': REDIS_IMAGE, 'instances': instances,
                        'labels': environment.labels, 'separate_databases': True}
            (output / 'instances.json').write_text(json.dumps(document, indent=2) + '\n', encoding='utf-8')
            check_wire(args.output.resolve(), document)
            while args.keep and not (output / 'stop').exists():
                time.sleep(.5)
    finally:
        execute('docker', 'rm', '-f', shared, check=False)


if __name__ == '__main__':
    main()
