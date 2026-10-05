"""Omitted allocation metadata, signed disk PUT and optional PDF preview gates.

Requires a freshly launched isolated/sanitized family. Local requests only.
"""
import argparse
import base64
import hashlib
import json
import os
import pathlib
import sys
import uuid


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root',type=pathlib.Path,required=True)
    parser.add_argument('--portable',type=pathlib.Path,required=True)
    args=parser.parse_args()
    os.environ['CAMPFIRE_PARITY_ROOT']=str(args.root.resolve())
    sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[2]))
    from probe import Client, INSTANCES, LABELS
    from media_probe import multipart, pdf
    records=[]
    for name,info in INSTANCES['instances'].items():
        client=Client(name+'-final',info)
        result={'implementation':name,'image_id':info['image_id'],'checks':[]}
        try:
            client.login()
            raw=b'final allocation integrity\n'
            blob={'blob':{'filename':'metadata-null.txt','content_type':'text/plain','byte_size':len(raw),'checksum':base64.b64encode(hashlib.md5(raw).digest()).decode()}}
            sample,body=client.request('allocate','POST','/rails/active_storage/direct_uploads',json.dumps(blob).encode(),{'Content-Type':'application/json','X-CSRF-Token':client.csrf})
            assert sample['status']==200,sample
            value=json.loads(body)
            assert value['metadata']=={},value['metadata']
            assert client.sql('SELECT metadata FROM active_storage_blobs WHERE id='+str(value['id']))==[{'metadata':None}]
            result['checks'].append('omitted metadata is JSON empty hash / SQL NULL')
            sample,_=client.request('put','PUT',value['direct_upload']['url'],raw,{'Content-Type':'text/plain'})
            assert sample['status']==204,sample
            assert client.sql('SELECT metadata FROM active_storage_blobs WHERE id='+str(value['id']))==[{'metadata':None}]
            result['checks'].append('signed disk PUT preserves unanalysed NULL metadata')
            marker='final-pdf-'+uuid.uuid4().hex
            boundary,data=multipart(client.csrf,marker,'no-preview-tools.pdf','application/pdf',pdf())
            sample,body=client.request('pdf','POST','/rooms/'+str(LABELS['rooms.hq'])+'/messages',data,{'Content-Type':'multipart/form-data; boundary='+boundary,'Accept':'text/vnd.turbo-stream.html','X-CSRF-Token':client.csrf})
            assert sample['status']==200,sample
            assert b'/representations/' not in body,'PDF emitted preview URL without available tools'
            rows=client.sql("SELECT b.id,(SELECT count(*) FROM active_storage_attachments p WHERE p.record_type='ActiveStorage::Blob' AND p.record_id=b.id AND p.name='preview_image') previews FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id=b.id AND a.record_type='Message' JOIN messages m ON m.id=a.record_id WHERE m.client_message_id='"+marker+"'")
            assert len(rows)==1 and rows[0]['previews']==0,rows
            result['checks'].append('no PDF preview markup/records without Poppler/MuPDF')
            result['passed']=True
        except Exception as error:
            result['passed']=False;result['error']=repr(error)
        finally:
            client.finish(result)
            records.append(result)
        print(name,result['passed'],result.get('error',''),flush=True)
    args.portable.parent.mkdir(parents=True,exist_ok=True)
    args.portable.write_text(json.dumps({'records':records,'passed':sum(len(row['checks']) for row in records)},indent=2)+'\n',encoding='utf-8')
    assert len(records)==3 and all(row['passed'] for row in records),records


if __name__=='__main__':
    main()
