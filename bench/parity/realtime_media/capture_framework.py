"""Copy read-only pinned runtime framework sources into this audit's evidence."""
import hashlib
import json
import pathlib
import subprocess
from probe import HERE, INSTANCES

container = INSTANCES['instances']['rails']['container']
root = '/usr/local/bundle/ruby/3.4.0/bundler/gems/rails-1a02651ac37f/'
paths = ['activestorage/config/routes.rb',
         'activestorage/app/controllers/active_storage/disk_controller.rb',
         'activestorage/app/controllers/active_storage/direct_uploads_controller.rb',
         'activestorage/app/controllers/active_storage/blobs/proxy_controller.rb',
         'activestorage/app/controllers/active_storage/blobs/redirect_controller.rb',
         'activestorage/app/controllers/active_storage/representations/proxy_controller.rb',
         'activestorage/app/models/active_storage/variant_with_record.rb',
         'activestorage/app/models/active_storage/variation.rb',
         'activestorage/app/models/active_storage/blob/analyzable.rb',
         'activestorage/app/models/active_storage/blob/representable.rb',
         'activestorage/app/models/active_storage/blob/identifiable.rb',
         'activestorage/app/jobs/active_storage/analyze_job.rb',
         'activestorage/app/jobs/active_storage/purge_job.rb',
         'activestorage/app/jobs/active_storage/mirror_job.rb',
         'activestorage/app/jobs/active_storage/transform_job.rb',
         'activestorage/app/jobs/active_storage/preview_image_job.rb',
         'activestorage/app/jobs/active_storage/base_job.rb',
         'activestorage/lib/active_storage/analyzer/video_analyzer.rb',
         'activestorage/lib/active_storage/analyzer/audio_analyzer.rb',
         'activestorage/lib/active_storage/analyzer/image_analyzer.rb',
         'activestorage/lib/active_storage/previewer/poppler_pdf_previewer.rb',
         'activestorage/lib/active_storage/previewer/mupdf_previewer.rb',
         'activestorage/lib/active_storage/service/disk_service.rb',
         'activestorage/lib/active_storage/engine.rb',
         'actioncable/lib/action_cable/connection/base.rb',
         'actioncable/lib/action_cable/connection/client_socket.rb',
         'actioncable/lib/action_cable/server/connections.rb']
output = HERE / 'runtime/framework'
output.mkdir(exist_ok=True)
inventory = []
for path in paths:
    result = subprocess.run(['docker', 'exec', container, 'cat', root + path], capture_output=True, check=False)
    if result.returncode:
        inventory.append({'path': root + path, 'missing': True})
    else:
        target = output / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(result.stdout)
        inventory.append({'path': root + path, 'evidence': str(target.relative_to(HERE)), 'sha256': hashlib.sha256(result.stdout).hexdigest()})
(output / 'inventory.json').write_text(json.dumps(inventory, indent=2) + '\n')
print(json.dumps(inventory, indent=2))
