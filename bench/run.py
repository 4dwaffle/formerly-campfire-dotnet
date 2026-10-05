"""Run pinned Campfire implementations on isolated Docker volumes and disjoint CPU sets.

This harness checks successful HTTP responses, but does not certify UI/feature parity.
Results are engineering measurements until the separate parity report passes.
"""
import argparse
import contextlib
import datetime
import gzip
import hashlib
import json
import pathlib
import re
import statistics
import subprocess
import threading
import time
import urllib.error
import urllib.request
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[1]
MANIFEST = json.loads((ROOT / "bench/manifest.json").read_text())
LABELS_PATH = ROOT / "bench/.seed/labels.json"
SEED = "campfire-dotnet-seed-default"
PARITY_IMAGE = "campfire-parity-dotnet"
LOADGEN_IMAGE = "campfire-dotnet-loadgen"
DOTNET_IMAGES = {"dotnet-original": "campfire-dotnet:original", "dotnet": "campfire-dotnet:app", "aot": "campfire-dotnet:aot"}


def execute(*args, capture=True, check=True):
    result = subprocess.run([str(a) for a in args], cwd=ROOT, check=check,
                            capture_output=capture, text=True, encoding="utf-8")
    return result.stdout.strip() if capture else result


def write(path, data):
    path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


def validate_cable(sample):
    if sample["ready"] != sample["clients"] or sample["failed"]:
        raise RuntimeError("Cable clients did not all remain connected/subscribed")
    for phase, successful_key in (("latency", "messages"), ("throughput", "posted")):
        data = sample[phase]
        counts = data["post_requests"]
        accounted = counts["connection_errors"] + counts["transport_errors"] + sum(counts["statuses"].values())
        if counts["attempted"] != accounted or counts["errors"] or not set(counts["statuses"]).issubset({"200", "201", "204"}):
            raise RuntimeError(f"Cable {phase} POST failures/accounting mismatch")
        if data["complete"] != data[successful_key] or counts["attempted"] != data[successful_key]:
            raise RuntimeError(f"Cable {phase} messages did not reach all requested clients")
        if data["post"]["n"] != data[successful_key]:
            raise RuntimeError(f"Cable {phase} POST latency count mismatch")


class Environment:
    def __init__(self, options, output):
        self.options = options
        self.output = output
        self.identifier = "cf-dotnet-" + uuid.uuid4().hex[:12]
        self.network = self.identifier
        self.app = self.identifier + "-app"
        self.volume = self.identifier + "-data"
        self.labels = json.loads(LABELS_PATH.read_text())
        self.base = f"http://127.0.0.1:{options.port}"
        self.resources = []

    def loadgen(self, command, *args):
        client = self.identifier + "-client"
        result = execute("docker", "run", "--rm", "--name", client, "--network", self.network,
                         "--cpuset-cpus", self.options.client_cpus, self.options.loadgen_image, command,
                         "--base", "http://benchmark-app:3000", *args)
        return json.loads(result)

    def data_stats(self):
        sql = "SELECT count(*) AS messages, (SELECT count(*) FROM message_search_index WHERE body MATCH '\"bench\" AND \"write\"') AS bench_fts_rows FROM messages"
        result = execute("docker", "run", "--rm", "--entrypoint", "sqlite3", "-v", f"{self.volume}:/data", PARITY_IMAGE,
                         "-json", "/data/db/production.sqlite3", sql)
        return json.loads(result)[0]

    def job_stats(self):
        # One atomic observation after HTTP workloads; jobs keep running, so
        # these counts describe backlog at this instant, not drained throughput.
        script = "local p=0;local w=redis.call('SMEMBERS',KEYS[4]);for _,v in ipairs(w) do p=p+redis.call('LLEN',ARGV[1]..':processing:'..v);end;return {redis.call('LLEN',KEYS[1]),redis.call('LLEN',KEYS[2]),redis.call('ZCARD',KEYS[3]),redis.call('LLEN','resque:failed'),p,#w}"
        prefix = "campfire_production:dotnet"
        values = json.loads(execute("docker", "exec", self.identifier + "-redis", "redis-cli", "--json", "EVAL", script, "4",
            "resque:queue:default", prefix + ":outbound-held", prefix + ":scheduled", prefix + ":workers", prefix))
        return dict(zip(("ready", "outbound_held", "scheduled", "failed", "processing", "workers"), values))

    def sample(self, stop):
        while not stop.is_set():
            try:
                lines = execute("docker", "stats", "--no-stream", "--format", "{{json .}}",
                                self.app, self.identifier + "-client", check=False)
                self.resources.extend({"timestamp": time.time(), **json.loads(line)} for line in lines.splitlines() if line.startswith("{"))
            except (ValueError, subprocess.SubprocessError):
                pass
            stop.wait(1)

    @contextlib.contextmanager
    def application(self, name):
        execute("docker", "network", "create", self.network)
        execute("docker", "volume", "create", self.volume)
        # All files stay on the Docker Linux filesystem. Windows bind mount performance is excluded.
        uid = 1654 if name in DOTNET_IMAGES else 1000
        execute("docker", "run", "--rm", "--entrypoint", "bash", "-v", f"{SEED}:/seed:ro",
                "-v", f"{self.volume}:/out", PARITY_IMAGE, "-c",
                f"cp -a /seed/. /out/ && chown -R {uid}:{uid} /out && "
                "sqlite3 /out/db/production.sqlite3 \"DELETE FROM push_subscriptions; DELETE FROM webhooks;\"")
        image = DOTNET_IMAGES[name] if name in DOTNET_IMAGES else MANIFEST[name]["image"]
        redis = self.identifier + "-redis"
        if name in DOTNET_IMAGES:
            execute("docker", "run", "-d", "--name", redis, "--network", self.network,
                    "--network-alias", "benchmark-redis", "redis:7.4", "redis-server", "--appendonly", "yes")
        args = ["docker", "run", "-d", "--name", self.app, "--network", self.network, "--network-alias", "benchmark-app",
                "--cpuset-cpus", self.options.server_cpus, "-p", f"127.0.0.1:{self.options.port}:3000",
                "--env-file", ROOT / "reference-rust/parity/.env.reference", "-e", "HTTP_PORT=3000", "-e", "TARGET_PORT=3001",
                "-e", "PORT=3001", "-e", "WEB_CONCURRENCY=3", "-e", "JOB_CONCURRENCY=3", "-e", "RAILS_LOG_LEVEL=warn",
                "-e", "ASPNETCORE_ENVIRONMENT=Production", "-e", "DOTNET_ENVIRONMENT=Production",
                "-e", "Logging__LogLevel__Default=Warning", "-e", "CAMPFIRE_STORAGE=/rails/storage",
                "-e", "CAMPFIRE_DELIVER_INTEGRATIONS=" + ("true" if getattr(self.options, "deliver_integrations", False) else "false"),
                "-v", f"{self.volume}:/rails/storage"]
        if name in DOTNET_IMAGES:
            args += ["-e", "ASPNETCORE_URLS=http://+:3000", "-e", "REDIS_URL=redis://benchmark-redis:6379/0",
                     "-e", "CAMPFIRE_FRAGMENT_GZIP=" + ("true" if (getattr(self.options, "baseline_fragment_gzip", 0) if name == "dotnet-baseline" else getattr(self.options, "fragment_gzip", 0)) else "false")]
        start = time.perf_counter()
        execute(*args, image)
        try:
            deadline = start + 90
            while True:
                try:
                    with urllib.request.urlopen(self.base + "/up", timeout=1) as response:
                        if response.status == 200:
                            break
                except (OSError, urllib.error.URLError):
                    if time.perf_counter() >= deadline:
                        raise RuntimeError(f"{name} did not become ready: {execute('docker', 'logs', self.app, check=False)[-8000:]}")
                    time.sleep(0.1)
            cold_ms = (time.perf_counter() - start) * 1000
            yield cold_ms, image
        finally:
            logged = subprocess.run(["docker", "logs", self.app], cwd=ROOT, capture_output=True, text=True, encoding="utf-8")
            log = logged.stdout + logged.stderr
            (self.output / f"{name}-{self.repetition}-server.log").write_text(log, encoding="utf-8")
            execute("docker", "rm", "-f", self.app, check=False)
            if name in DOTNET_IMAGES:
                execute("docker", "rm", "-f", redis, check=False)
            # Only the freshly generated per-run resources are removed; the seed is preserved.
            execute("docker", "volume", "rm", self.volume, check=False)
            execute("docker", "network", "rm", self.network, check=False)

    def suite(self, name, repetition):
        self.repetition = repetition
        self.resources = []
        with self.application(name) as (cold_ms, image):
            cookie = self.loadgen("login", "--email", self.labels["emails.david"], "--password", self.labels["passwords.all"])["cookie"]
            room = str(self.labels["rooms.watercooler"])
            scrape = self.loadgen("scrape", "--cookie", cookie, "--room", room)
            if scrape["status"] != 200:
                raise RuntimeError(f"{name}: authenticated room returned {scrape['status']}")
            captures = {}
            routes = {
                "room": f"/rooms/{room}",
                "messages": f"/rooms/{room}/messages?before={self.labels['messages.busy_060']}",
                "sidebar": "/users/me/sidebar",
                "search": "/searches?q=coffee",
                "up": "/up"
            }
            for key, path in routes.items():
                request = urllib.request.Request(self.base + path, headers={"Cookie": cookie, "Accept-Encoding": "identity"})
                with urllib.request.urlopen(request) as response:
                    body = response.read()
                    captures[key] = {"status": response.status, "bytes": len(body), "sha256": hashlib.sha256(body).hexdigest(),
                                     "headers": dict(response.headers)}
                    captures[key]["message_ids"] = sorted(set(re.findall(rb'data-message-id="([0-9]+)"', body)))
                    captures[key]["message_ids"] = [item.decode() for item in captures[key]["message_ids"]]
                    (self.output / f"{name}-{repetition}-{key}.html").write_bytes(body)
                compressed_request = urllib.request.Request(self.base + path, headers={"Cookie": cookie, "Accept-Encoding": "gzip"})
                with urllib.request.urlopen(compressed_request) as response:
                    encoded = response.read()
                    encoding = response.headers.get("Content-Encoding", "identity").lower()
                    decoded = gzip.decompress(encoded) if encoding == "gzip" else encoded
                    if encoding not in ("identity", "gzip") or response.status != 200:
                        raise RuntimeError(f"{name}/{key}: unexpected compression response {response.status}/{encoding}")
                    ids = sorted(set(item.decode() for item in re.findall(rb'data-message-id="([0-9]+)"', decoded)))
                    if ids != captures[key]["message_ids"]:
                        raise RuntimeError(f"{name}/{key}: gzip message IDs differ")
                    captures[key]["gzip_response"] = {"encoding": encoding, "bytes": len(encoded), "decoded_bytes": len(decoded), "sha256": hashlib.sha256(encoded).hexdigest(), "headers": dict(response.headers)}
                    (self.output / f"{name}-{repetition}-{key}.encoded").write_bytes(encoded)
            metrics = []
            warmups = []
            protocol_errors = []
            before = self.data_stats()
            stop = threading.Event()
            sampler = threading.Thread(target=self.sample, args=(stop,), daemon=True)
            sampler.start()
            try:
                if "http" in self.options.suites:
                    routes["post_message"] = "POST"
                    for key, path in routes.items():
                        if key not in self.options.routes:
                            continue
                        args = ["--cookie", cookie, "--gzip", str(self.options.gzip)]
                        if key == "post_message":
                            args += ["--post-room", str(self.labels["rooms.hq"]), "--csrf", scrape.get("csrf") or ""]
                        else:
                            args += ["--path", path]
                        warmup = self.loadgen("http", *args, "--conc", "4", "--duration", str(self.options.warmup))
                        warmup["route"] = key
                        allowed = {"200"} if key != "post_message" else {"200", "201", "204"}
                        if warmup.get("errors", 0) or not set(warmup["statuses"]).issubset(allowed):
                            write(self.output / f"{name}-{repetition}-warmup-failure.json", warmup)
                            raise RuntimeError(f"{name}/{key}: warmup failures")
                        warmups.append(warmup)
                        for concurrency in self.options.concurrencies:
                            sample = self.loadgen("http", *args, "--conc", str(concurrency), "--duration", str(self.options.duration))
                            sample["route"] = key
                            allowed = {"200"} if key != "post_message" else {"200", "201", "204"}
                            if sample.get("errors", 0) or not set(sample["statuses"]).issubset(allowed):
                                write(self.output / f"{name}-{repetition}-failure.json", sample)
                                raise RuntimeError(f"{name}/{key}: failures: {sample['statuses']}, {sample.get('errors')}")
                            metrics.append(sample)
                            print(f"{name} rep={repetition} {key} c={concurrency}: {sample['rps']} req/s", flush=True)
                cable = []
                if "cable" in self.options.suites:
                    for clients in self.options.cable_clients:
                        sample = self.loadgen("cable", "--cookie", cookie, "--room", room, "--csrf", scrape.get("csrf") or "",
                                              "--streams", ",".join(scrape["streams"]), "--clients", str(clients),
                                              "--tput-secs", str(self.options.duration), "--posters", "4")
                        cable.append(sample)
                        try:
                            validate_cable(sample)
                        except RuntimeError as error:
                            sample['validation_error'] = str(error)
                            protocol_errors.append(f"Cable/{clients}: {error}")
                            write(self.output / f"{name}-{repetition}-cable-{clients}-failure.json", sample)
                        print(f"{name} rep={repetition} cable clients={clients}: {sample['throughput']['delivered_msgs_per_sec']} delivered msg/s", flush=True)
                upload = None
                if "upload" in self.options.suites:
                    # Loadgen needs the original JPEG inside its container; a separate command mounts fixtures read-only.
                    image_file = "/fixtures/black_hole.jpg"
                    upload_command = execute("docker", "run", "--rm", "--name", self.identifier + "-client", "--network", self.network,
                        "--cpuset-cpus", self.options.client_cpus, "-v", f"{ROOT / 'upstream/test/fixtures/files'}:/fixtures:ro",
                        self.options.upload_image, "--base", "http://benchmark-app:3000", "--cookie", cookie,
                        "--room", str(self.labels["rooms.hq"]), "--csrf", scrape.get("csrf") or "", "--file", image_file, "--reps", "5", check=False)
                    upload = json.loads(upload_command)
                    if upload.get("error_count") or upload.get("success_count") != 5:
                        write(self.output / f"{name}-{repetition}-upload-failure.json", upload)
                        protocol_errors.append("Upload/thumbnail failures")
                    print(f"{name} rep={repetition} upload+thumbnail: {upload.get('median_total_ms')} ms, {upload.get('error_count')} errors", flush=True)
            finally:
                stop.set()
                sampler.join(timeout=5)
            after = self.data_stats()
            expected_writes = sum(sum(s["statuses"].values()) for s in warmups + metrics if s["route"] == "post_message")
            expected_writes += sum(sum(count for status, count in s[phase]['post_requests']['statuses'].items() if status in {'200','201','204'}) for s in cable for phase in ('latency','throughput'))
            expected_writes += upload.get('success_count', 0) if upload else 0
            if after["messages"] - before["messages"] != expected_writes:
                error = f"{name}: persisted message count mismatch; before={before}, after={after}, expected={expected_writes}"
                if 'http' in self.options.suites:
                    raise RuntimeError(error)
                protocol_errors.append(error)
            if "http" in self.options.suites:
                expected_fts = sum(sum(s["statuses"].values()) for s in warmups + metrics if s["route"] == "post_message")
                if after["bench_fts_rows"] - before["bench_fts_rows"] != expected_fts:
                    raise RuntimeError(f"{name}: persisted messages/FTS mismatch; before={before}, after={after}, successful posts={expected_writes}")
            result = {"app": name, "repetition": repetition, "image": image, "cold_start_ms": cold_ms,
                      "parity_status": "unverified: HTTP success is not feature or UI parity", "captures": captures,
                      "http": metrics, "warmups": warmups, "database_validation": {"before": before, "after": after, "expected_writes": expected_writes},
                      "cable": cable, "upload": upload, "protocol_validation_errors": protocol_errors, "resource_samples": self.resources}
            if name in DOTNET_IMAGES:
                result["durable_jobs_after_http"] = self.job_stats()
            write(self.output / f"{name}-{repetition}.json", result)
            return result


def report(output, results):
    names = list(dict.fromkeys(result["app"] for result in results))
    keys = list(dict.fromkeys((sample["route"], sample["conc"]) for result in results for sample in result["http"]))
    rows = ["# Local engineering benchmark", "", "These measurements do not certify feature or UI parity. Do not publish a speedup claim until the separate parity checks pass.", "",
            "| Workload | Clients | " + " | ".join(names) + " |", "|---|---:|" + "---:|" * len(names)]
    for route, concurrency in keys:
        cells = []
        for name in names:
            values = [sample["rps"] for result in results if result["app"] == name for sample in result["http"]
                      if sample["route"] == route and sample["conc"] == concurrency]
            cells.append(f"{statistics.median(values):,.1f} ({min(values):,.1f}–{max(values):,.1f})" if values else "—")
        rows.append(f"| {route} | {concurrency} | " + " | ".join(cells) + " |")
    rows += ["", "Cells are median requests/second (minimum–maximum). Raw JSON includes latency, statuses, response sizes and resource samples.",
             "", "All apps use fresh copies of one Rails-generated seed on Docker Linux volumes. CPU sets and exact images are recorded in metadata.json. Docker Desktop bridge networking is part of these measurements."]
    rows += ["", "## Response work", "", "| App | Room bytes / messages | Page bytes / messages | Search bytes / messages | Sidebar bytes |", "|---|---:|---:|---:|---:|"]
    for name in names:
        result = next(r for r in results if r["app"] == name)
        captures = result["captures"]
        cells = [f"{captures[k]['bytes']:,} / {len(captures[k]['message_ids'])}" for k in ("room", "messages", "search")]
        rows.append(f"| {name} | " + " | ".join(cells) + f" | {captures['sidebar']['bytes']:,} |")
    rows += ["", "Sizes above are uncompressed captures. Message ID sets must match the pinned Rails responses. HTML differs between implementations; equal business records do not establish equal rendering work or full parity.",
             "", "## Latency", "", "| Workload | Clients | " + " | ".join(names) + " |", "|---|---:|" + "---:|" * len(names)]
    for route, concurrency in keys:
        cells = []
        for name in names:
            samples = [sample for r in results if r["app"] == name for sample in r["http"] if sample["route"] == route and sample["conc"] == concurrency]
            cells.append(f"{statistics.median(s['latency']['p50_ms'] for s in samples):.2f} / {statistics.median(s['latency']['p99_ms'] for s in samples):.2f}" if samples else "—")
        rows.append(f"| {route} | {concurrency} | " + " | ".join(cells) + " |")
    rows += ["", "Latency cells: median p50 / median p99 in milliseconds. These are closed-loop load measurements, without coordinated-omission correction.",
             "", "## Container footprint", "", "| App | Container start to healthy (ms) | Peak working set (MiB) | Image size (MiB) |", "|---|---:|---:|---:|"]
    metadata = json.loads((output / "metadata.json").read_text())
    def mib(value):
        match = re.match(r"([0-9.]+)([A-Za-z]+)", value.strip())
        return float(match[1]) * {"B": 1/1048576, "KiB": 1/1024, "MiB": 1, "GiB": 1024}[match[2]] if match else 0
    for name in names:
        runs = [r for r in results if r["app"] == name]
        peaks = [max((mib(s["MemUsage"].split("/")[0]) for s in r["resource_samples"] if s.get("Name", "").endswith("-app")), default=0) for r in runs]
        rows.append(f"| {name} | {statistics.median(r['cold_start_ms'] for r in runs):.0f} | {statistics.median(peaks):.1f} | {metadata['images'][name]['Size']/1048576:.1f} |")
    rows += ["", "Startup includes Docker launch and readiness polling, not just application initialization. Working set is the median of per-run peak Docker stats samples. Images include media tools. CPU affinity does not isolate other host workloads; background containers are recorded in metadata."]
    if keys:
        rows += ["", "## HTTP error counts", "", "| App | Measured responses | Warmup responses | Transport errors | Unexpected statuses |", "|---|---:|---:|---:|---:|"]
        for name in names:
            measured = [s for r in results if r['app'] == name for s in r['http']]
            warmups = [s for r in results if r['app'] == name for s in r['warmups']]
            unexpected = sum(count for s in measured + warmups for status, count in s['statuses'].items() if status not in ({'200','201','204'} if s['route'] == 'post_message' else {'200'}))
            rows.append(f"| {name} | {sum(sum(s['statuses'].values()) for s in measured):,} | {sum(sum(s['statuses'].values()) for s in warmups):,} | {sum(s['errors'] for s in measured + warmups):,} | {unexpected:,} |")
    clients = list(dict.fromkeys(s['clients'] for r in results for s in r['cable']))
    if clients:
        rows += ["", "## WebSocket fanout", "", "| App | Clients | Observed delivered messages/s | Complete / posted | All-client p99 (ms) | Ready / requested | POST errors | Connection failures | Valid runs |", "|---|---:|---:|---:|---:|---:|---:|---:|---:|"]
        for name in names:
            for client_count in clients:
                samples = [s for r in results if r['app'] == name for s in r['cable'] if s['clients'] == client_count]
                if not samples:
                    continue
                rows.append(f"| {name} | {client_count} | {statistics.median(s['throughput']['delivered_msgs_per_sec'] for s in samples):,.1f} | {sum(s['throughput']['complete'] for s in samples)} / {sum(s['throughput']['posted'] for s in samples)} | {statistics.median(s['throughput']['all_clients']['p99_ms'] for s in samples):.2f} | {sum(s['ready'] for s in samples)} / {sum(s['clients'] for s in samples)} | {sum(s[p]['post_requests']['errors'] for s in samples for p in ('latency','throughput'))} | {sum(s['failed'] for s in samples)} | {sum('validation_error' not in s for s in samples)} / {len(samples)} |")
        rows += ["", "One delivered message means its marked content reached every requested subscriber. Failed/incomplete runs are retained and explicitly marked; their observed rate is not a valid capacity result, and their p99 covers only completed deliveries. POST statuses/attempts, full readiness and delivery counts are checked. WebSocket compression is not requested. All clients share the seeded user's session. Server broadcast architecture and payloads differ."]
    if any(r['upload'] for r in results):
        rows += ["", "## Upload and actual attachment thumbnail", "", "| App | Successful uploads | Errors | POST median (ms) | Thumbnail median (ms) | Total median (ms) | Dimensions |", "|---|---:|---:|---:|---:|---:|---|"]
        for name in names:
            uploads = [r['upload'] for r in results if r['app'] == name and r['upload']]
            if not uploads:
                continue
            samples = [s for u in uploads for s in u.get('runs', []) if s.get('success')]
            if not samples:
                rows.append(f"| {name} | 0 | {sum(u['error_count'] for u in uploads)} | — | — | — | — |")
                continue
            dimensions = ', '.join(sorted(set(f"{s['image']['width']}x{s['image']['height']}" for s in samples if s.get('success'))))
            rows.append(f"| {name} | {sum(u['success_count'] for u in uploads)} | {sum(u['error_count'] for u in uploads)} | {statistics.median(s['post_ms'] for s in samples):.1f} | {statistics.median(s['thumb_ms'] for s in samples):.1f} | {statistics.median(s['total_ms'] for s in samples):.1f} | {dimensions} |")
        rows += ["", "Each trial uploads the original 3840x2160 JPEG, selects the uploaded attachment rather than its creator avatar, follows its Active Storage redirects and verifies MIME, image magic and resized dimensions. Encoders, resulting bytes and job scheduling differ; raw JSON records the full responses and hashes."]
    (output / "report.md").write_text("\n".join(rows) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apps", default="rails,rust,dotnet,aot")
    parser.add_argument("--jit-image", help="Explicit JIT image for reproducible comparisons")
    parser.add_argument("--aot-image", help="Explicit AOT image for reproducible comparisons")
    parser.add_argument("--baseline-image", help="Preserved JIT image exposed as dotnet-baseline for an alternating comparison")
    parser.add_argument("--baseline-source-sha256", help="Recorded runtime source hash of the preserved baseline image")
    parser.add_argument("--loadgen-image", default=LOADGEN_IMAGE)
    parser.add_argument("--upload-image", default="campfire-dotnet-upload-client")
    parser.add_argument("--reps", type=int, default=3)
    parser.add_argument("--duration", type=float, default=8)
    parser.add_argument("--warmup", type=float, default=3)
    parser.add_argument("--concurrencies", default="1,16,64")
    parser.add_argument("--server-cpus", default="0-3")
    parser.add_argument("--client-cpus", default="4-7")
    parser.add_argument("--port", type=int, default=4390)
    parser.add_argument("--gzip", type=int, choices=[0, 1], default=1)
    parser.add_argument("--fragment-gzip", type=int, choices=[0, 1], default=1,
                        help="Enable .NET fragment gzip reuse; disable for a same-image compression comparison")
    parser.add_argument("--baseline-fragment-gzip", type=int, choices=[0, 1], default=0,
                        help="Fragment gzip setting for the preserved dotnet-baseline image")
    parser.add_argument("--suites", default="http")
    parser.add_argument("--routes", default="room,messages,sidebar,search,up,post_message",
                        help="HTTP workloads to measure; actual response captures still cover every read route")
    parser.add_argument("--cable-clients", default="100,1000")
    parser.add_argument("--output", type=pathlib.Path)
    options = parser.parse_args()
    if options.jit_image: DOTNET_IMAGES["dotnet"] = options.jit_image
    if options.aot_image: DOTNET_IMAGES["aot"] = options.aot_image
    if options.baseline_image:
        if not options.baseline_source_sha256 or not re.fullmatch(r"[0-9a-f]{64}", options.baseline_source_sha256):
            parser.error("a preserved baseline requires its recorded runtime source SHA-256")
        DOTNET_IMAGES["dotnet-baseline"] = options.baseline_image
    options.apps = options.apps.split(",")
    options.suites = options.suites.split(",")
    options.routes = options.routes.split(",")
    if not options.routes or len(set(options.routes)) != len(options.routes) or not set(options.routes).issubset({"room", "messages", "sidebar", "search", "up", "post_message"}):
        parser.error("invalid or duplicate HTTP route")
    options.concurrencies = [int(value) for value in options.concurrencies.split(",")]
    options.cable_clients = [int(value) for value in options.cable_clients.split(",")]
    if any(app not in ("rails", "rust", *DOTNET_IMAGES) for app in options.apps) or options.reps < 1 or options.duration <= 0 or options.warmup <= 0 or any(c < 1 for c in options.concurrencies):
        parser.error("invalid app, duration, repetitions or concurrency")
    output = options.output or ROOT / "bench/results" / datetime.datetime.now(datetime.UTC).strftime("%Y%m%dT%H%M%SZ")
    output.mkdir(parents=True, exist_ok=False)
    metadata = {"date_utc": datetime.datetime.now(datetime.UTC).isoformat(), "options": {k: str(v) if isinstance(v, pathlib.Path) else v for k, v in vars(options).items()},
                "manifest": MANIFEST, "docker": json.loads(execute("docker", "info", "--format", "{{json .}}")),
                "images": {name: json.loads(execute("docker", "image", "inspect", DOTNET_IMAGES[name] if name in DOTNET_IMAGES else MANIFEST[name]["image"]))[0] for name in options.apps},
                "background_containers": execute("docker", "ps", "--format", "{{.Names}} {{.Image}}"),
                "loadgen_image": json.loads(execute("docker", "image", "inspect", options.loadgen_image))[0],
                "upload_image": json.loads(execute("docker", "image", "inspect", options.upload_image))[0] if 'upload' in options.suites else None,
                "harness_sha256": hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
                "upload_client_sha256": hashlib.sha256((ROOT / 'bench/upload.py').read_bytes()).hexdigest(),
                "loadgen_instrumentation": json.loads((ROOT / 'bench/.work/instrumented-loadgen/instrumentation.json').read_text()) if 'cable' in options.suites else None,
                "application_source_sha256": hashlib.sha256(b"".join(p.relative_to(ROOT).as_posix().encode() + b"\0" + p.read_bytes() for p in sorted((ROOT / "src/Campfire").rglob("*")) if p.is_file() and not {"bin", "obj"}.intersection(p.parts))).hexdigest(),
                "application_runtime_source_sha256": hashlib.sha256(b"".join(p.relative_to(ROOT).as_posix().encode() + b"\0" + p.read_bytes() for p in sorted((ROOT / "src/Campfire").rglob("*")) if p.is_file() and p.suffix.lower() != ".md" and not {"bin", "obj"}.intersection(p.parts))).hexdigest(),
                "seed_database_sha256": hashlib.sha256((ROOT / "bench/.seed/production.sqlite3").read_bytes()).hexdigest()}
    metadata["runtime_source_sha256_by_app"] = {
        name: options.baseline_source_sha256 if name == "dotnet-baseline" else metadata["application_runtime_source_sha256"]
        for name in options.apps if name == "dotnet-baseline" or name in ("dotnet", "aot")
    }
    write(output / "metadata.json", metadata)
    results = []
    for repetition in range(1, options.reps + 1):
        order = options.apps if repetition % 2 else list(reversed(options.apps))
        for name in order:
            environment = Environment(options, output)
            current = environment.suite(name, repetition)
            previous = next((r for r in results if r["app"] == "rails"), results[0] if results else None)
            if previous:
                for route in ("room", "messages", "search"):
                    if current["captures"][route]["message_ids"] != previous["captures"][route]["message_ids"]:
                        raise RuntimeError(f"{name}/{route}: response message ID mismatch")
            results.append(current)
            report(output, results)


if __name__ == "__main__":
    main()
