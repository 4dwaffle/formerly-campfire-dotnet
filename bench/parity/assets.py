"""Verify every manifest asset's served bytes, without browser automation."""
import concurrent.futures
import argparse
import hashlib
import json
import pathlib
import urllib.error
import urllib.request
HERE=pathlib.Path(__file__).resolve().parent
ROOT=HERE.parents[1]

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest',type=pathlib.Path,default=HERE/'browser/runtime/instances.json')
    parser.add_argument('--output',type=pathlib.Path,default=HERE/'browser/results')
    args=parser.parse_args()
    args.output.mkdir(parents=True,exist_ok=True)
    instances=json.loads(args.manifest.read_text())['instances']
    manifest=json.loads((ROOT/'src/Campfire/wwwroot/manifest.json').read_text())
    paths=sorted({v['digested_path'] for v in manifest.values()})
    def fetch(item):
        name,instance,path=item
        req=urllib.request.Request(instance['base']+'/assets/'+path,headers={'User-Agent':'Mozilla/5.0 Chrome/131.0.0.0 Safari/537.36'})
        try:
            response=urllib.request.urlopen(req,timeout=20)
        except urllib.error.HTTPError as e: response=e
        body=response.read()
        file=ROOT/'src/Campfire/wwwroot/assets'/path
        local=file.read_bytes() if file.exists() else None
        return {'application':name,'path':path,'status':response.code,'sha256':hashlib.sha256(body).hexdigest(),'matches_copied_asset':body==local if local is not None else None,'bytes':len(body),'cache_control':response.headers.get('Cache-Control'),'content_type':response.headers.get('Content-Type')}
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        records=list(pool.map(fetch,((name,instance,path) for name,instance in instances.items() for path in paths)))
    (args.output/'assets.json').write_text(json.dumps(records,indent=2)+'\n')
    summary={name:{'assets':sum(r['application']==name for r in records),'errors':sum(r['application']==name and r['status']!=200 for r in records),'byte_differences':sum(r['application']==name and not r['matches_copied_asset'] for r in records)} for name in instances}
    print(json.dumps(summary,indent=2))
    original=json.loads((HERE/'browser/results/rails-assets-manifest.json').read_text())
    paths=sorted({v['digested_path'] for v in original.values()})
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        records=list(pool.map(fetch,((name,instance,path) for name,instance in instances.items() for path in paths)))
    (args.output/'original-assets.json').write_text(json.dumps(records,indent=2)+'\n')
    original_summary={name:{'original_paths':len(paths),'errors':sum(r['application']==name and r['status']!=200 for r in records),'byte_differences':sum(r['application']==name and not r['matches_copied_asset'] for r in records)} for name in instances}
    print(json.dumps(original_summary,indent=2))
    (args.output/'assets-summary.json').write_text(json.dumps({'manifest':summary,'original_manifest':original_summary},indent=2)+'\n')
    return 1 if any(s['errors'] or s['byte_differences'] for s in [*summary.values(),*original_summary.values()]) else 0

if __name__=='__main__':raise SystemExit(main())
