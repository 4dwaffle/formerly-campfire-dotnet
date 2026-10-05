"""Compare real room response sizes and rendered form fields."""
import argparse
import json
import pathlib
import re
import sys
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parent/'identity'))
from check import Client

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest',type=pathlib.Path,required=True)
    parser.add_argument('--output',type=pathlib.Path,required=True)
    args=parser.parse_args()
    manifest=json.loads(args.manifest.read_text())
    summary={}
    args.output.mkdir(parents=True,exist_ok=True)
    for name,instance in manifest['instances'].items():
        client=Client(instance)
        client.login(manifest['labels'],'david')
        record=client.request('profile-response','/rooms/'+str(manifest['labels']['rooms.watercooler']))
        body=record['body']
        summary[name]={'status':record['status'],'bytes':len(body.encode()),'csrf_inputs':body.count('name="authenticity_token"'),'forms':body.count('<form'),'messages':len(re.findall(r'id="message_[^"]*"',body)),'image_id':instance['image_id']}
        (args.output/(name+'-room-response.json')).write_text(json.dumps(record,indent=2)+'\n')
    (args.output/'response-sizes.json').write_text(json.dumps(summary,indent=2)+'\n')
    print(json.dumps(summary,indent=2))

if __name__=='__main__':main()
