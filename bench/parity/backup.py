"""Verify online backup and restore only on disposable browser-family volumes."""
import json
import pathlib
import subprocess
import argparse
HERE = pathlib.Path(__file__).resolve().parent

def run(*args):
    r = subprocess.run(list(args),capture_output=True,text=True)
    return {'command':list(args),'exit_code':r.returncode,'stdout':r.stdout,'stderr':r.stderr}

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest',type=pathlib.Path,default=HERE/'browser/runtime/instances.json')
    parser.add_argument('--output',type=pathlib.Path,default=HERE/'browser/results/backup.json')
    parser.add_argument('--marker',default='parity-browser-20261005')
    args=parser.parse_args()
    if not all(c.isalnum() or c in '-_' for c in args.marker): raise ValueError('Marker must contain only letters, numbers, hyphens, and underscores.')
    manifest=json.loads(args.manifest.read_text())
    result={}
    for name,instance in manifest['instances'].items():
        hook='/rails/hooks/pre-backup' if name=='rails' else '/hooks/pre-backup'
        pre=run('docker','exec',instance['container'],hook)
        inspect=run('docker','run','--rm','--entrypoint','sqlite3','-v',instance['volume']+':/data:ro','campfire-parity-dotnet','-json','file:/data/backups/production.sqlite3?immutable=1',f'PRAGMA integrity_check; SELECT count(*) AS messages FROM messages; SELECT count(*) AS browser_messages FROM message_search_index WHERE body MATCH \'"{args.marker}"\';')
        # Restore is checked on a fresh copy, never under a live database connection.
        volume='cf-parity-restore-'+name+'-'+instance['volume'].split('-')[2]
        create=run('docker','volume','create',volume)
        try:
            restore=run('docker','run','--rm','--user','0','--entrypoint','bash','-e','CAMPFIRE_STORAGE=/rails/storage','-v',instance['volume']+':/source:ro','-v',volume+':/rails/storage',instance['image'],'-c','cp -a /source/. /rails/storage/ && '+('/rails/hooks/post-restore' if name=='rails' else '/hooks/post-restore'))
            verify=run('docker','run','--rm','--entrypoint','sqlite3','-v',volume+':/data:ro','campfire-parity-dotnet','-json','file:/data/db/production.sqlite3?immutable=1','PRAGMA integrity_check; SELECT count(*) AS messages FROM messages;')
        finally:
            cleanup=run('docker','volume','rm',volume)
        result[name]={'prepare':pre,'snapshot':inspect,'create':create,'restore':restore,'verify':verify,'cleanup':cleanup}
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(result,indent=2)+'\n')
    valid=all(all(part['exit_code']==0 for part in data.values()) and '"integrity_check":"ok"' in data['snapshot']['stdout'] and '"integrity_check":"ok"' in data['verify']['stdout'] and '"browser_messages":1' in data['snapshot']['stdout'] for data in result.values())
    print(json.dumps({'valid':valid,'applications':list(result)}))
    return 0 if valid else 1

if __name__ == '__main__':raise SystemExit(main())
