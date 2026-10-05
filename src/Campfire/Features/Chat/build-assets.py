"""Compile immutable Rails frontend inputs to this feature's wwwroot directory."""
from pathlib import Path

root = Path(__file__).resolve().parents[4]
source = (root / 'reference-go/bin/build-assets').read_text()
source = source.replace("ROOT=Path(__file__).resolve().parents[1]", "ROOT=Path(" + repr(str(root)) + ")")
source = source.replace("RUST=ROOT/'reference/crates/assets'", "RUST=ROOT/'reference-rust/crates/assets'")
source = source.replace("RAILS=ROOT/'reference/reference'", "RAILS=ROOT/'upstream'")
source = source.replace("OUT=ROOT/'assets/generated'", "OUT=ROOT/'src/Campfire/Features/Chat/generated-assets'")
# There are no Go overrides in this checkout. Rust's asset overrides only replace
# server-dependent imports, not the Campfire controllers or application CSS.
source = source.replace("paths=[ROOT/'assets/overrides',RUST/'overrides']", "paths=[RUST/'overrides']")
# Campfire's original multipart uploader supplies the Rails CSRF header. Rust's
# override removes it for its own forgery policy; that override is incompatible
# with this port's real Rails token verification.
source = source.replace("if p.is_file() and not p.name.startswith('.'):", "if p.is_file() and not p.name.startswith('.') and not (folder == RUST/'overrides' and p.relative_to(folder).as_posix() == 'models/file_uploader.js'):")
exec(compile(source, 'pinned-asset-compiler', 'exec'), {'__name__': '__main__', '__file__': __file__})
import shutil
generated = root / 'src/Campfire/Features/Chat/generated-assets'
public = root / 'src/Campfire/wwwroot'
shutil.copytree(generated / 'public', public, dirs_exist_ok=True)
# Keep rendering metadata beside served assets so publish includes it.
for name in ('manifest.json', 'stylesheets.html', 'importmap.html'):
    shutil.copyfile(generated / name, public / name)
