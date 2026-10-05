"""Capture model/template HTTP validator vectors from the pinned Rails image, without serving HTTP."""
import argparse
import json
import subprocess
from pathlib import Path

IMAGE = 'ghcr.io/basecamp/once-campfire@sha256:7197fff46e15d0dce69e0a16624df239e1b9bd069534dc28dec67ab7b6771672'
RUBY = r'''
ActiveRecord::Base.establish_connection(adapter: "sqlite3", database: ":memory:")
ActiveRecord::Schema.define do
  create_table(:messages) { |t| t.timestamps }
end
controller = MessagesController.new
controller.set_request!(ActionDispatch::TestRequest.create)
controller.set_response!(ActionDispatch::TestResponse.new)
controller.action_name = "index"
controller.lookup_context.formats = [:html]
digest = controller.send(:lookup_and_digest_template, "messages/index")
vectors = [[{id: 7, updated_at: "2026-10-05 07:08:09.123456"}], [{id: 7, updated_at: "2026-10-05 07:08:09.123456"}, {id: 8, updated_at: "2026-10-05 07:08:10.000000"}]]
puts JSON.generate({image: ENV.fetch("PARITY_IMAGE"), template_digest: digest, vectors: vectors.map { |rows|
  messages = rows.map { |row| Message.instantiate(row.stringify_keys) }
  validators = controller.send(:combine_etags, messages, {last_modified: messages.map(&:updated_at).max, public: false, template: nil})
  response = ActionDispatch::Response.new
  response.weak_etag = validators
  {messages: rows, cache_keys: messages.map(&:cache_key_with_version), expanded_key: ActiveSupport::Cache.expand_cache_key(validators), etag: response.etag}
}, variants: [{flash: "Room not found or inaccessible"}, {frame: "user_sidebar"}, {flash: "Room not found or inaccessible", frame: "user_sidebar"}].map { |variant|
  controller.request.set_header("action_dispatch.request.flash_hash", ActionDispatch::Flash::FlashHash.new(variant[:flash] ? {"alert" => variant[:flash]} : {}))
  controller.request.headers["Turbo-Frame"] = variant[:frame]
  messages = vectors.first.map { |row| Message.instantiate(row.stringify_keys) }
  validators = controller.send(:combine_etags, messages, {last_modified: messages.map(&:updated_at).max, public: false, template: nil})
  response = ActionDispatch::Response.new
  response.weak_etag = validators
  variant.merge(expanded_key: ActiveSupport::Cache.expand_cache_key(validators), etag: response.etag)
}})
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = subprocess.run(['docker', 'run', '--rm', '-i', '-e', 'SECRET_KEY_BASE=parity-vector-local-only', '-e', 'PARITY_IMAGE=' + IMAGE, '--entrypoint', 'bundle', IMAGE, 'exec', 'rails', 'runner', '-'], input=RUBY, encoding='utf-8', text=True, capture_output=True, check=True)
    payload = json.loads(result.stdout.splitlines()[-1])
    args.output.write_text(json.dumps(payload, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(payload, indent=2))


if __name__ == '__main__':
    main()
