"""Generate real authenticated room requests during a diagnostic trace."""
import argparse
import concurrent.futures
import gzip
import json
import pathlib
import sys
import time
import urllib.request

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent / 'identity'))
from check import Client

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=pathlib.Path, required=True)
    parser.add_argument('--application', default='dotnet')
    parser.add_argument('--duration', type=float, default=20)
    parser.add_argument('--concurrency', type=int, default=16)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text())
    client = Client(manifest['instances'][args.application])
    client.login(manifest['labels'], 'david')
    url = client.instance['base'] + '/rooms/' + str(manifest['labels']['rooms.watercooler'])
    deadline = time.monotonic() + args.duration
    def worker(_):
        requests = errors = encoded_bytes = decoded_bytes = 0
        while time.monotonic() < deadline:
            try:
                request = urllib.request.Request(url, headers={'User-Agent':'Mozilla/5.0 Chrome/131.0.0.0 Safari/537.36','Accept':'text/html','Accept-Encoding':'gzip'})
                with client.opener.open(request, timeout=30) as response:
                    data = response.read()
                    body = gzip.decompress(data) if response.headers.get('Content-Encoding') == 'gzip' else data
                    if response.status != 200 or b'id="message_' not in body or b'Write a message' not in body: errors += 1
                    requests += 1
                    encoded_bytes += len(data)
                    decoded_bytes += len(body)
            except Exception: errors += 1
        return requests, errors, encoded_bytes, decoded_bytes
    started = time.monotonic()
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.concurrency) as pool:
        rows = list(pool.map(worker, range(args.concurrency)))
    elapsed = time.monotonic() - started
    result = {'purpose':'diagnostic load, not an isolated benchmark','image_id':client.instance['image_id'],'duration_seconds':elapsed,'concurrency':args.concurrency,
              'requests':sum(r[0] for r in rows),'errors':sum(r[1] for r in rows),'encoded_bytes':sum(r[2] for r in rows),'decoded_bytes':sum(r[3] for r in rows)}
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result))
    return 1 if result['errors'] else 0

if __name__ == '__main__':raise SystemExit(main())
