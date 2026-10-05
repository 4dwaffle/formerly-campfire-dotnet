"""Prepare pinned reference sources, production images, loadgen and Rails-generated seeds."""
import argparse
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
MANIFEST = json.loads((ROOT / "bench/manifest.json").read_text())
SEED_VOLUME = "campfire-dotnet-seed-default"


def run(*args):
    subprocess.run([str(a) for a in args], cwd=ROOT, check=True)


def sources():
    for source in MANIFEST.values():
        folder = ROOT / source["directory"]
        if not folder.exists():
            run("git", "clone", source["repository"], folder)
        revision = subprocess.check_output(["git", "-C", str(folder), "rev-parse", "HEAD"], text=True).strip()
        if revision != source["revision"]:
            raise RuntimeError(f"{folder} is at {revision}; expected {source['revision']}. Preserve local work and check out the pinned revision first.")


def images():
    for name in ("rails", "rust"):
        run("docker", "pull", MANIFEST[name]["image"])
    run("docker", "tag", MANIFEST["rails"]["image"], "campfire-reference:app")
    run("docker", "build", "-t", "campfire-parity-dotnet", "-f", ROOT / "bench/Dockerfile.parity",
        "--build-context", f"fixtures={ROOT / 'upstream/test/fixtures'}", ROOT)
    run("docker", "build", "-t", "campfire-dotnet-loadgen", "-f", ROOT / "bench/Dockerfile.loadgen", ROOT / "reference-rust/bench/loadgen")
    run("docker", "build", "-t", "campfire-dotnet:app", ROOT)
    run("docker", "build", "-f", ROOT / "Dockerfile.aot", "-t", "campfire-dotnet:aot", ROOT)


def seed():
    # Reuse complete seeds; an interrupted preparation can resume without deleting its volume.
    probe = subprocess.run(["docker", "volume", "inspect", SEED_VOLUME], capture_output=True)
    if probe.returncode != 0:
        run("docker", "volume", "create", SEED_VOLUME)
    command = ["docker", "run", "--rm", "--user", "0:0", "--env-file", ROOT / "reference-rust/parity/.env.reference",
               "-e", "PARITY_REDIS=1", "-e", "PARITY_WORK=/work", "-e", "RAILS_LOG_LEVEL=warn",
               "-v", f"{SEED_VOLUME}:/rails/storage", "-v", f"{ROOT}:/work:ro", "campfire-parity-dotnet"]
    complete = subprocess.run(["docker", "run", "--rm", "--entrypoint", "bash", "-v", f"{SEED_VOLUME}:/rails/storage",
                               "campfire-parity-dotnet", "-c", "test -s /rails/storage/db/labels.json"], capture_output=True).returncode == 0
    if not complete:
        run(*command, "bin/rails", "db:prepare")
        run(*command, "bin/rails", "runner", "/work/reference-rust/parity/seeds/build.rb", "default")
    folder = ROOT / "bench/.seed"
    folder.mkdir(parents=True, exist_ok=True)
    container = subprocess.check_output(["docker", "create", "-v", f"{SEED_VOLUME}:/rails/storage", "campfire-parity-dotnet"], text=True).strip()
    try:
        run("docker", "cp", f"{container}:/rails/storage/db/labels.json", folder / "labels.json")
        run("docker", "cp", f"{container}:/rails/storage/db/production.sqlite3", folder / "production.sqlite3")
        run("docker", "cp", f"{container}:/rails/storage/files", folder / "files")
    finally:
        run("docker", "rm", container)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("steps", nargs="*", choices=["sources", "images", "seed"])
    options = parser.parse_args()
    for step in options.steps or ["sources", "images", "seed"]:
        globals()[step]()
