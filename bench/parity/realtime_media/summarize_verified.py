"""Preserve final observations and capture hashes outside ignored runtime paths."""
import argparse
import hashlib
import json
import pathlib


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root',type=pathlib.Path,required=True)
    parser.add_argument('--output',type=pathlib.Path,required=True)
    args=parser.parse_args()
    runtime=args.root/'runtime'
    instances=json.loads((runtime/'instances.json').read_text())
    mixed=args.root/'mixed/runtime'
    mixed_instances=json.loads((mixed/'instances.json').read_text())
    wire=json.loads((mixed/'results.json').read_text())
    assert wire['passed']==27 and all(item['passed'] for item in wire['checks'])
    records=[]
    for name,info in instances['instances'].items():
        folder=runtime/(name+'-media')
        value=json.loads((folder/'results.json').read_text())
        assert 'error' not in value,value.get('error')
        samples={row['label']:row for row in value['http']}
        assert value['post_status']==200
        assert value['thumbnail']=={'format':'png','content_type':'image/png','width':2,'height':2}
        assert value['before_thumbnail_fetch'][0]['variant_count']==1
        assert value['after_thumbnail_fetch']==[{'count':1}]
        assert samples['thumbnail']['status']==302 and samples['thumbnail_bytes']['status']==200
        assert samples['representation_legacy_alias']['status']==302
        assert samples['representation_nested_filename']['status']==302
        assert samples['representation_proxy']['status']==200
        assert value['pdf']['blobs'][0]['preview_count']==0
        assert value['spoof']['blobs'][0]['content_type']=='image/png'
        records.append({'implementation':name,'image_id':info['image_id'],'http':value['http'],
                        'thumbnail':value['thumbnail'],'eager_variants':1,'pdf_previews':0,
                        'spoof_identified_as':'image/png','guarded_subscription':value['guarded']['type'],
                        'stock_channel_bypass':value['stock_bypass']['type'],
                        'cable_packet_count':len(value['cable_packets']),
                        'raw_capture_sha256':hashlib.sha256((folder/'results.json').read_bytes()).hexdigest()})
    thumbnails=[next(row for row in record['http'] if row['label']=='thumbnail_bytes') for record in records]
    assert len({row['sha256'] for row in thumbnails})==1 and all(row['bytes']==287 for row in thumbnails)
    result={'source_runtime_root':args.root.as_posix(),'server_cpus':instances['server_cpus'],
            'mixed_checks':wire,'mixed_images':{name:info['image_id'] for name,info in mixed_instances['instances'].items()},
            'mixed_raw_captures':[{'path':path.relative_to(mixed).as_posix(),'bytes':path.stat().st_size,
                                   'sha256':hashlib.sha256(path.read_bytes()).hexdigest()}
                                  for path in sorted(mixed.rglob('*-cable.json'))],
            'media':records,'http_responses':sum(len(record['http']) for record in records),
            'non_success':sum(row['status']>=400 for record in records for row in record['http'])}
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print('PASS portable evidence: 27 mixed controls,',result['http_responses'],'media responses, zero errors, identical real PNG')


if __name__=='__main__':
    main()
