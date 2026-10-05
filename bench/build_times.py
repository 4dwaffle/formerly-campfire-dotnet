"""Measure complete uncached and cached Docker image builds on an isolated builder."""
import argparse
import datetime
import hashlib
import io
import json
import pathlib
import re
import subprocess
import tarfile
import time
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[1]


def command(arguments, log):
    started = datetime.datetime.now(datetime.UTC).isoformat()
    timer = time.perf_counter()
    with log.open("w", encoding="utf-8") as stream:
        result = subprocess.run(arguments, cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT)
    return {
        "command": arguments, "started_utc": started,
        "wall_seconds": time.perf_counter() - timer, "exit_code": result.returncode,
        "log": log.name, "log_sha256": hashlib.sha256(log.read_bytes()).hexdigest(),
    }


def steps(log):
    found = {}
    for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
        header = re.match(r"#(\d+) \[([^\]]+)\] (.*)", line)
        if header:
            found[header[1]] = {"stage": header[2], "instruction": header[3]}
        done = re.match(r"#(\d+) (?:DONE ([0-9.]+)s|CACHED)$", line)
        if done and done[1] in found:
            found[done[1]]["seconds"] = float(done[2]) if done[2] else 0.0
            found[done[1]]["cached"] = done[2] is None
    return list(found.values())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--apps", default="jit,aot,rust")
    args = parser.parse_args()
    apps = args.apps.split(",")
    if not apps or len(set(apps)) != len(apps) or not set(apps) <= {"jit", "aot", "rust"}:
        parser.error("apps must be unique jit,aot,rust entries")
    args.output.mkdir(parents=True, exist_ok=False)
    builder = "campfire-build-time-" + uuid.uuid4().hex[:12]
    manifest = json.loads((ROOT / "bench/manifest.json").read_text())
    record = {
        "builder": builder, "repository_commit": subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        "method": "One complete --no-cache image build, then one unchanged cached build per app. "
                  "Isolated BuildKit builder starts with empty layer and cache-mount state; "
                  "no global cache is pruned. Base/dependency downloads, media tools, compilation, "
                  "image export and Docker loading are included. Mutable base tags follow each Dockerfile. "
                  "BuildKit step times are diagnostic and can overlap; do not sum them. "
                  "Rust builds libvips/FFmpeg from source while .NET installs packages, so total image "
                  "times are not equivalent compiler workloads. All host CPUs are available to builds.",
        "runs": [],
    }
    def save():
        (args.output / "measurements.json").write_text(json.dumps(record, indent=2) + "\n")
    created = False
    try:
        setup = command(["docker", "buildx", "create", "--name", builder,
                         "--driver", "docker-container"], args.output / "builder-create.log")
        record["builder_creation"] = setup
        save()
        if setup["exit_code"]:
            raise RuntimeError("builder creation failed; see log")
        created = True
        bootstrap = command(["docker", "buildx", "inspect", builder, "--bootstrap"],
                            args.output / "builder-bootstrap.log")
        record["builder_bootstrap"] = bootstrap
        save()
        if bootstrap["exit_code"]:
            raise RuntimeError("builder bootstrap failed; see log")
        for app in apps:
            context = ROOT / "reference-rust" if app == "rust" else ROOT
            tag = "campfire-build-time:" + builder.removeprefix("campfire-build-time-") + "-" + app
            if app == "rust":
                revision = subprocess.check_output(["git", "-C", str(context), "rev-parse", "HEAD"], text=True).strip()
                if revision != manifest["rust"]["revision"]:
                    raise RuntimeError("Rust checkout differs from pinned revision")
                # Export pinned objects to our own context; never initialize or
                # otherwise modify the read-only reference checkouts.
                link = subprocess.check_output(["git", "-C", str(context), "ls-tree", "HEAD", "reference"], text=True)
                reference_revision = link.split()[2]
                snapshot = ROOT / "bench/.work" / builder / "rust-context"
                snapshot.mkdir(parents=True, exist_ok=False)
                for repository, commit, destination in (
                    (context, revision, snapshot),
                    (ROOT / "upstream", reference_revision, snapshot / "reference"),
                ):
                    destination.mkdir(parents=True, exist_ok=True)
                    archive = subprocess.check_output(["git", "-C", str(repository), "archive", commit])
                    with tarfile.open(fileobj=io.BytesIO(archive)) as content:
                        content.extractall(destination, filter="data")
                record["rust_context"] = {"revision": revision, "reference_revision": reference_revision,
                                          "path": str(snapshot.relative_to(ROOT)),
                                          "preparation_excluded_from_build_time": True}
                context = snapshot
            dockerfile = context / ("Dockerfile.aot" if app == "aot" else "Dockerfile")
            base = ["docker", "buildx", "build", "--builder", builder, "--load",
                    "--progress", "plain", "--file", str(dockerfile), "--tag", tag]
            if app == "rust":
                base += ["--build-arg", "GIT_REVISION=" + revision, "--build-arg", "APP_VERSION=parity"]
            for mode in ("uncached", "cached"):
                print(f"Building {app}: {mode}", flush=True)
                log = args.output / f"{app}-{mode}.log"
                current = command(base + (["--no-cache"] if mode == "uncached" else []) + [str(context)], log)
                current.update(app=app, mode=mode, dockerfile_sha256=hashlib.sha256(dockerfile.read_bytes()).hexdigest(),
                               buildkit_steps=steps(log))
                if current["exit_code"] == 0:
                    current["image"] = json.loads(subprocess.check_output(
                        ["docker", "image", "inspect", tag], text=True))[0]
                record["runs"].append(current)
                save()
                print(f"{app} {mode}: {current['wall_seconds']:.2f}s, exit={current['exit_code']}", flush=True)
                if current["exit_code"]:
                    print(f"Failed build retained in {log}; continuing other apps", flush=True)
                    break
    finally:
        if created:
            record["builder_cleanup"] = command(["docker", "buildx", "rm", builder], args.output / "builder-cleanup.log")
            names = subprocess.check_output(["docker", "buildx", "ls", "--format", "{{.Name}}"], text=True)
            record["builder_removed"] = builder not in names.splitlines()
        save()


if __name__ == "__main__":
    main()
