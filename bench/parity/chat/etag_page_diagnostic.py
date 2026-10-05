"""Reconstruct a captured ordered page validator with actual pinned Rails models."""
import json
import re
import sqlite3
import subprocess
from pathlib import Path

setup = json.loads(Path('bench/parity/remediation/final/chat/runtime/instances.json').read_text())
body = Path('bench/parity/chat/runtime/remediation-final-main/rails/david-message-page.body').read_text(encoding='utf-8')
ids = list(dict.fromkeys(map(int, re.findall(r'data-message-id="(\d+)"', body))))
connection = sqlite3.connect('file:bench/.seed/production.sqlite3?mode=ro', uri=True)
rows = [dict(zip(['id', 'updated_at'], connection.execute('select id,updated_at from messages where id=?', [id]).fetchone())) for id in ids]
ruby = r'''
controller = MessagesController.new
controller.set_request!(ActionDispatch::TestRequest.create)
controller.set_response!(ActionDispatch::TestResponse.new)
controller.action_name = "index"
controller.request.set_header("action_dispatch.request.flash_hash", ActionDispatch::Flash::FlashHash.new("alert" => "Room not found or inaccessible"))
messages = ROWS.map { |row| Message.instantiate(row.stringify_keys) }
validators = controller.send(:combine_etags,messages,{last_modified:messages.map(&:updated_at).max,public:false,template:nil})
response=ActionDispatch::Response.new
response.weak_etag=validators
puts JSON.generate({formats:controller.lookup_context.formats,etagger_sources:controller.etaggers.map(&:source_location),keys:messages.map(&:cache_key_with_version),expanded_key:ActiveSupport::Cache.expand_cache_key(validators),etag:response.etag})
'''.replace('ROWS', 'JSON.parse(' + json.dumps(json.dumps(rows)) + ')')
result = subprocess.run(['docker', 'exec', '-i', setup['instances']['rails']['container'], 'bundle', 'exec', 'rails', 'runner', '-'], input=ruby, text=True, encoding='utf-8', capture_output=True, check=True)
Path('bench/parity/chat/etag-page-diagnostic.json').write_text(json.dumps(json.loads(result.stdout.splitlines()[-1]), indent=2) + '\n', encoding='utf-8')
print(result.stdout)
