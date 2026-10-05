"""Compare captured .NET HTML against a baseline, normalizing only fresh CSRF values."""
import argparse
import gzip
import json
import pathlib
import re

CSRF = re.compile(rb'(<(?:input|meta)\b[^>]*\bname="(?:authenticity_token|csrf-token)"[^>]*\b(?:value|content)=")[^"]*(")')


def compare(folder):
    options = json.loads((folder / "metadata.json").read_text())["options"]
    if "dotnet-baseline" not in options["apps"]:
        raise ValueError("An explicit dotnet-baseline is required")
    checks = []
    for app in options["apps"]:
        if app == "dotnet-baseline":
            continue
        if app not in ("dotnet", "aot"):
            continue  # Different implementations require their own semantic audit.
        for repetition in range(1, options["reps"] + 1):
            for route in ("room", "messages", "search", "sidebar", "up"):
                for extension in ("html", "encoded"):
                    def read(name):
                        body = (folder / f"{name}-{repetition}-{route}.{extension}").read_bytes()
                        if body.startswith(b"\x1f\x8b"):
                            body = gzip.decompress(body)
                        return CSRF.sub(rb'\1[fresh mask]\2', body)
                    equal = read("dotnet-baseline") == read(app)
                    checks.append({"app": app, "repetition": repetition, "route": route,
                                   "representation": extension, "equal_except_csrf_values": equal})
    result = {"normalization": "Only authenticity_token input and csrf-token meta value attributes; all other decoded bytes must match.",
              "excluded_implementations": [app for app in options["apps"] if app not in ("dotnet-baseline", "dotnet", "aot")],
              "passed": sum(check["equal_except_csrf_values"] for check in checks), "checks": checks}
    if not checks:
        raise ValueError("At least one .NET candidate is required")
    (folder / "response-equivalence.json").write_text(json.dumps(result, indent=2) + "\n")
    assert result["passed"] == len(checks), [check for check in checks if not check["equal_except_csrf_values"]]
    return result["passed"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folders", type=pathlib.Path, nargs="+")
    args = parser.parse_args()
    for folder in args.folders:
        print(json.dumps({"folder": str(folder), "comparisons_passed": compare(folder)}))
