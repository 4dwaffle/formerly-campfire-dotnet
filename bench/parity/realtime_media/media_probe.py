"""Actual multipart media and signed-URL behavior; only disposable local apps."""
import json
import pathlib
import re
import struct
import sys
import uuid
import zlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
from upload import multipart, AttachmentParser, image_info
from probe import Client, INSTANCES, LABELS, HERE, Cable


def png():
    def chunk(tag, data):
        return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data))
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 2, 2, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(b'\0\xff\0\0\0\xff\0' * 2)) + chunk(b'IEND', b'')


def pdf():
    stream = b'1 0 0 rg 0 0 50 50 re f\n'
    objects = [b'<< /Type /Catalog /Pages 2 0 R >>', b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
               b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 50 50] /Resources << >> /Contents 4 0 R >>',
               b'<< /Length ' + str(len(stream)).encode() + b' >>\nstream\n' + stream + b'endstream']
    result = b'%PDF-1.4\n'; offsets = [0]
    for number, value in enumerate(objects, 1):
        offsets.append(len(result))
        result += str(number).encode() + b' 0 obj\n' + value + b'\nendobj\n'
    xref = len(result)
    result += b'xref\n0 5\n0000000000 65535 f \n' + b''.join(f'{offset:010} 00000 n \n'.encode() for offset in offsets[1:])
    return result + b'trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n' + str(xref).encode() + b'\n%%EOF\n'


def main():
    for name, info in INSTANCES['instances'].items():
        client = Client(name, info)
        client.output = HERE / 'runtime' / (name + '-media')
        client.output.mkdir(exist_ok=True)
        room = LABELS['rooms.david_and_jason']
        client.sql(f"INSERT OR IGNORE INTO memberships(user_id,room_id,involvement,created_at,updated_at) VALUES({LABELS['users.david']},{LABELS['rooms.hq']},'everything',datetime('now'),datetime('now'))")
        client.login()
        results = {}
        # Render real existing JPEG and MP4 attachments from the seeded Rails DB.
        for label in ('image', 'image_large', 'video', 'file_bmp'):
            _, body = client.request('seed_' + label, 'GET', '/rooms/' + str(LABELS['rooms.hq']) + '/@' + str(LABELS['messages.' + label]))
            # Whole room captures retained; media routes below target new attachment only.
        cable = Cable(client)
        try:
            cable.wait('welcome')
            _, room_body = client.request('direct_room', 'GET', '/rooms/' + str(room))
            signed = re.search(rb'channel="RoomMessagesChannel" signed-stream-name="([^"]+)"', room_body)[1].decode()
            identifier = {'channel': 'RoomMessagesChannel', 'signed_stream_name': signed}
            cable.send('subscribe', identifier)
            results['guarded'] = cable.wait('confirm_subscription')
            cable.send('subscribe', {'channel': 'Turbo::StreamsChannel', 'signed_stream_name': signed})
            results['stock_bypass'] = cable.wait('reject_subscription')
            client_id = 'parity-' + uuid.uuid4().hex
            boundary, data = multipart(client.csrf, client_id, 'audit.png', 'image/png', png())
            sample, body = client.request('multipart_png', 'POST', '/rooms/' + str(room) + '/messages', data,
                                          {'Content-Type': 'multipart/form-data; boundary=' + boundary,
                                           'Accept': 'text/vnd.turbo-stream.html, text/html', 'X-CSRF-Token': client.csrf})
            results['post_status'] = sample['status']
            results['broadcast'] = cable.wait('message')
            rows = client.sql("SELECT m.id,a.blob_id,b.metadata,(SELECT count(*) FROM active_storage_variant_records v WHERE v.blob_id=b.id) variant_count FROM messages m JOIN active_storage_attachments a ON a.record_type='Message' AND a.record_id=m.id JOIN active_storage_blobs b ON b.id=a.blob_id WHERE m.client_message_id='" + client_id + "'")
            results['before_thumbnail_fetch'] = rows
            parser = AttachmentParser(client_id)
            parser.feed(body.decode())
            attached = parser.attachment()
            sample, _ = client.request('thumbnail', 'GET', attached['src'])
            location = sample['headers'].get('Location') or sample['headers'].get('location')
            if location:
                sample, encoded = client.request('thumbnail_bytes', 'GET', location)
                results['thumbnail'] = image_info(encoded)
            results['after_thumbnail_fetch'] = client.sql('SELECT count(*) count FROM active_storage_variant_records WHERE blob_id=' + str(rows[0]['blob_id']))
            # Framework aliases, filename glob and true proxy-vs-redirect behavior.
            client.request('representation_legacy_alias', 'GET', attached['src'].replace('/representations/redirect/', '/representations/'))
            client.request('representation_proxy', 'GET', attached['src'].replace('/representations/redirect/', '/representations/proxy/'))
            client.request('representation_nested_filename', 'GET', attached['src'].rsplit('/', 1)[0] + '/folder/audit.png')
            # Use same .NET/Rails signed seed avatar contracts and actual bytes.
            client.request('logo', 'GET', '/account/logo')
            client.request('avatar', 'GET', '/users/' + LABELS['avatar_tokens.david'] + '/avatar')
            for extension, mime, raw in [('pdf', 'application/pdf', pdf()), ('spoof', 'text/html', png()), ('mathml', 'application/mathml+xml', b'<math>local</math>')]:
                identity = 'parity-' + uuid.uuid4().hex
                boundary, data = multipart(client.csrf, identity, 'audit.' + extension, mime, raw)
                sample, _ = client.request('multipart_' + extension, 'POST', '/rooms/' + str(room) + '/messages', data,
                                           {'Content-Type': 'multipart/form-data; boundary=' + boundary, 'Accept': 'text/vnd.turbo-stream.html', 'X-CSRF-Token': client.csrf})
                results[extension] = {'status': sample['status'], 'blobs': client.sql("SELECT b.id,b.key,b.content_type,b.metadata,(SELECT count(*) FROM active_storage_attachments p WHERE p.record_type='ActiveStorage::Blob' AND p.record_id=b.id AND p.name='preview_image') preview_count FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id=b.id JOIN messages m ON m.id=a.record_id AND a.record_type='Message' WHERE m.client_message_id='" + identity + "'")}
        except Exception as error:
            results['error'] = repr(error)
        finally:
            results['cable_packets'] = cable.packets
            cable.close()
            client.finish(results)
            print(name, json.dumps(results), flush=True)


if __name__ == '__main__':
    main()
