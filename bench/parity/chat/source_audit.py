"""Read-only chat source and compiled-asset inventory; writes evidence only here."""
import argparse
import datetime
import difflib
import hashlib
import json
import pathlib
import posixpath
import re
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[3]
HERE = pathlib.Path(__file__).resolve().parent


def sha(data):
    return hashlib.sha256(data).hexdigest()


def lines(path, needle):
    return [{"line": i, "text": text.strip()} for i, text in enumerate((ROOT / path).read_text(encoding="utf-8").splitlines(), 1) if needle in text]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=pathlib.Path, default=HERE / "source.json")
    args = parser.parse_args()
    manifest = json.loads((ROOT / "src/Campfire/wwwroot/manifest.json").read_text())
    differences = []
    assets = []
    # Propshaft rewrites local CSS URLs / RAILS_ASSET_URL, without changing logic.
    ws = r"[ \t\n\x0b\x0c\r]"
    patterns = {
        "css": re.compile(r'url\(' + ws + r'''*["']?(?!(?:\#|%23|data:|http:|https:|//))([^"' \t\n\x0b\x0c\r?#)]+)([#?][^"')]+)?''' + ws + r'''*["']?\)'''),
        "js": re.compile(r'RAILS_ASSET_URL\(' + ws + r'''*["']?(?!(?:\#|%23|data|http|//))([^"' \t\n\x0b\x0c\r?#)]+)([#?][^"')]+)?''' + ws + r'''*["']?\)'''),
    }
    for folder in ("upstream/app/javascript", "upstream/app/assets/stylesheets", "upstream/app/assets/images", "upstream/app/assets/audios"):
        source_dir = ROOT / folder
        if not source_dir.exists():
            continue
        for source in sorted(source_dir.rglob("*")):
            if not source.is_file() or source.name.startswith("."):
                continue
            logical = source.relative_to(source_dir).as_posix()
            if logical not in manifest:
                differences.append({"logical": logical, "source": source.relative_to(ROOT).as_posix(), "status": "missing"})
                continue
            target = ROOT / "src/Campfire/wwwroot/assets" / manifest[logical]["digested_path"]
            original = source.read_bytes()
            expected = original
            pattern = patterns.get(source.suffix.removeprefix("."))
            if pattern:
                def rewrite(match):
                    relative = match[1]
                    resolved = relative.lstrip("/") if relative.startswith("/") else posixpath.normpath(posixpath.join(posixpath.dirname(logical), relative))
                    location = "/assets/" + manifest[resolved]["digested_path"] + (match[2] or "") if resolved in manifest else relative
                    value = '"' + location + '"'
                    return "url(" + value + ")" if source.suffix == ".css" else value
                expected = pattern.sub(rewrite, original.decode("utf-8")).encode("utf-8")
            actual = target.read_bytes() if target.exists() else b""
            entry = {"logical": logical, "source": source.relative_to(ROOT).as_posix(), "served": target.relative_to(ROOT).as_posix(), "source_sha256": sha(original), "expected_compiled_sha256": sha(expected), "served_sha256": sha(actual), "status": "match" if expected == actual else "different"}
            assets.append(entry)
            if expected != actual:
                differences.append({**entry, "diff": "\n".join(difflib.unified_diff(expected.decode("utf-8", "replace").splitlines(), actual.decode("utf-8", "replace").splitlines(), fromfile=entry["source"], tofile=entry["served"], lineterm="")) if source.suffix in {".js", ".css", ".svg"} else "binary difference"})
    feature = "src/Campfire/Features/Chat/ChatFeature.cs"
    renderer = "src/Campfire/Features/Chat/ChatRenderer.cs"
    anchors = {
        "routes": (feature, "routes.Map"),
        "frame_layout": (renderer, "Turbo-Frame"),
        "edit_raw_body": (renderer, "value=\"{{E(message.Body)}}"),
        "bot_input": (feature, "Request.HasFormContentType"),
        "etag": (feature, "IfNoneMatch"),
        "same_host_embed": ("src/Campfire/Features/Chat/RichText.cs", "SafeWebUrl"),
        "timestamp_json": ("src/Campfire/Features/Chat/RichText.cs", "yyyy-MM-ddTHH:mm:ss.ffffffZ"),
        "refresh_casing": (feature, "Turbo(\"replace\""),
    }
    evidence = {name: {"path": path, "matches": lines(path, needle)} for name, (path, needle) in anchors.items()}
    result = {"date_utc": datetime.datetime.now(datetime.UTC).isoformat(), "rails_revision": subprocess.check_output(["git", "-C", str(ROOT / "upstream"), "rev-parse", "HEAD"], text=True).strip(), "rails_worktree_status": subprocess.check_output(["git", "-C", str(ROOT / "upstream"), "status", "--short"], text=True).strip(), "assets_compared": len(assets), "asset_matches": sum(item["status"] == "match" for item in assets), "asset_differences": differences, "assets": assets, "source_anchors": evidence}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(args.output), "assets_compared": len(assets), "asset_matches": result["asset_matches"], "differences": [item["logical"] for item in differences]}))


if __name__ == "__main__":
    main()
