"""Keep one disposable JIT seed clone alive for diagnostic profiling."""
import argparse
import contextlib
import json
import pathlib
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from run import Environment, DOTNET_IMAGES, execute

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=4471)
    parser.add_argument('--image', default='campfire-dotnet:app')
    parser.add_argument('--server-cpus', default='0-3')
    parser.add_argument('--fragment-gzip', type=int, choices=[0, 1], default=1)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    DOTNET_IMAGES['dotnet'] = args.image
    options = argparse.Namespace(port=args.port, server_cpus=args.server_cpus, deliver_integrations=False, fragment_gzip=args.fragment_gzip)
    environment = Environment(options, args.output)
    environment.repetition = 'profile'
    with environment.application('dotnet') as (ready, image):
        instance = {'base':environment.base, 'container':environment.app, 'volume':environment.volume, 'network':environment.network,
                    'image':image, 'image_id':json.loads(execute('docker','image','inspect',image))[0]['Id'], 'ready_ms':ready}
        document = {'instances':{'dotnet':instance}, 'labels':environment.labels, 'server_cpus':args.server_cpus, 'fragment_gzip':args.fragment_gzip}
        (args.output/'instances.json').write_text(json.dumps(document,indent=2)+'\n')
        print(environment.app, flush=True)
        while not (args.output/'stop').exists(): time.sleep(.5)

if __name__=='__main__': main()
