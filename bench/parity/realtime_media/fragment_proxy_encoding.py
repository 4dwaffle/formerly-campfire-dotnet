"""Compare pinned front-proxy vs direct Rack/Puma encoding; local fixture only."""
import argparse
import json
import pathlib
import subprocess
from fragment_gzip_live import Client

parser=argparse.ArgumentParser(); parser.add_argument('--root',type=pathlib.Path,required=True); parser.add_argument('--output',type=pathlib.Path,required=True); args=parser.parse_args()
document=json.loads((args.root/'instances.json').read_text()); info=document['instances']['rails']
client=Client(info,args.root/'proxy-vs-rack'); client.login(document['labels'])
cookie='; '.join(value.name+'='+value.value for value in client.jar)
path='/rooms/'+str(document['labels']['rooms.watercooler'])
records=[]
ruby='''cfg=JSON.parse(STDIN.read); http=Net::HTTP.new("127.0.0.1",3001,nil); r=Net::HTTP::Get.new(cfg["path"]); r["Cookie"]=cfg["cookie"]; r["Accept-Encoding"]=cfg["accept"]; s=http.request(r); puts JSON.generate({status:s.code.to_i,encoding:s["content-encoding"],bytes:s.body.bytesize,request_accept_encoding:r["Accept-Encoding"]})'''
for accept in ['identity;q=1,gzip;q=0.5','gzip;q=invalid','gzip;q=0','gzip']:
    front,_=client.request('front-'+str(len(records)),'GET',path,headers={'Accept-Encoding':accept})
    direct=subprocess.run(['docker','exec','-i',info['container'],'bundle','exec','ruby','-rnet/http','-rjson','-e',ruby],input=json.dumps({'path':path,'cookie':cookie,'accept':accept}),capture_output=True,text=True,check=True)
    records.append({'accept_encoding':accept,'front_thrust':{'status':front['status'],'encoding':front['headers'].get('content-encoding'),'wire_bytes':front['wire_bytes'],'actual_request_header':front['request_accept_encoding']},'direct_puma_rack':json.loads(direct.stdout)})
client.save()
source_ruby='''s=Gem::Specification.find_by_name("rack"); puts "rack #{s.version}"; {"conditional_get.rb"=>[70,10],"deflater.rb"=>[45,35],"utils.rb"=>[273,43]}.each { |f,(i,n)| p=File.join(s.full_gem_path,"lib/rack",f); puts p; puts File.readlines(p)[i,n].map.with_index { |v,j| "#{i+j+1}: #{v}" } };puts "thruster #{Gem::Specification.find_by_name("thruster").version}"'''
source=subprocess.run(['docker','exec',info['container'],'bundle','exec','ruby','-e',source_ruby],capture_output=True,text=True,check=True)
args.output.with_suffix('.source.txt').write_text(source.stdout)
report={'image_id':info['image_id'],'records':records,'source_excerpt':str(args.output.with_suffix('.source.txt')).replace('\\','/'),'real_external_contacts':False}
args.output.write_text(json.dumps(report,indent=2)+'\n'); print(json.dumps(report))
