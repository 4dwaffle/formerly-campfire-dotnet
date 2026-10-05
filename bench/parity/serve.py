"""Launch isolated disposable Rails/JIT/AOT instances for one parity auditor."""
import argparse
import contextlib
import datetime
import json
import pathlib
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from run import Environment, DOTNET_IMAGES, MANIFEST, execute


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--family', required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--output', type=pathlib.Path, help='Separate runtime metadata and logs from a previous audit.')
    parser.add_argument('--jit-image')
    parser.add_argument('--aot-image')
    parser.add_argument('--fragment-gzip', type=int, choices=[0, 1], default=1)
    parser.add_argument('--server-cpus', default='0-3')
    parser.add_argument('--client-cpus', default='4-7')
    parser.add_argument('--deliver-integrations', action='store_true', help='Process jobs only after launcher removes all seeded external endpoints.')
    args = parser.parse_args()
    if args.jit_image: DOTNET_IMAGES['dotnet'] = args.jit_image
    if args.aot_image: DOTNET_IMAGES['aot'] = args.aot_image
    output = args.output or pathlib.Path(__file__).resolve().parent / args.family / 'runtime'
    output.mkdir(parents=True, exist_ok=True)
    instances = {}
    with contextlib.ExitStack() as stack:
        for offset, name in enumerate(('rails', 'dotnet', 'aot')):
            options = argparse.Namespace(port=args.port + offset, server_cpus=args.server_cpus, client_cpus=args.client_cpus,
                                         loadgen_image='campfire-dotnet-loadgen', deliver_integrations=args.deliver_integrations,
                                         fragment_gzip=args.fragment_gzip)
            environment = Environment(options, output)
            environment.repetition = 'parity'
            started, image = stack.enter_context(environment.application(name))
            instances[name] = {'base':environment.base, 'container':environment.app,
                               'volume':environment.volume, 'network':environment.network,
                               'image':image, 'image_id':json.loads(execute('docker','image','inspect',image))[0]['Id'],
                               'ready_ms':started}
        document = {'started_utc':datetime.datetime.now(datetime.UTC).isoformat(), 'family':args.family,
                    'instances':instances, 'server_cpus':args.server_cpus, 'labels':json.loads((pathlib.Path(__file__).resolve().parents[1]/'.seed/labels.json').read_text())}
        (output/'instances.json').write_text(json.dumps(document,indent=2)+'\n',encoding='utf-8')
        print(json.dumps(document),flush=True)
        try:
            while not (output/'stop').exists():
                time.sleep(0.5)
        finally:
            print('Cleaning up disposable parity containers/volumes/networks',flush=True)


if __name__ == '__main__':
    main()
