#!/usr/bin/env python3
"""Measure real Campfire multipart upload and its attached Active Storage thumbnail.

Uses the fields/Accept header from the pinned reference Rust upload client, but
selects the attachment inside the newly created message rather than the avatar.
Stdlib only. No server fixture shortcuts and no third-party redirect requests.
"""

import argparse
import hashlib
import json
import pathlib
import secrets
import statistics
import struct
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zlib
from html.parser import HTMLParser


class ValidationError(ValueError):
    pass


def image_info(data):
    """Read encoded pixel dimensions from PNG, JPEG or WebP container headers."""
    if data.startswith(b"\x89PNG\r\n\x1a\n"):
        if len(data) < 33 or data[8:16] != b"\x00\x00\x00\rIHDR":
            raise ValidationError("invalid PNG IHDR")
        width, height = struct.unpack(">II", data[16:24])
        if zlib.crc32(data[12:29]) != struct.unpack(">I", data[29:33])[0]:
            raise ValidationError("invalid PNG IHDR checksum")
        result = {"format": "png", "content_type": "image/png", "width": width, "height": height}
    elif data.startswith(b"\xff\xd8"):
        offset = 2
        dimensions = None
        has_scan = False
        # JPEG dimensions are in a Start Of Frame, before the entropy-coded scan.
        sof = {0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF}
        while offset < len(data):
            if data[offset] != 0xFF:
                raise ValidationError("invalid JPEG marker")
            while offset < len(data) and data[offset] == 0xFF:
                offset += 1
            if offset >= len(data):
                break
            marker = data[offset]
            offset += 1
            if marker in {0xD9, 0xDA}:
                has_scan = marker == 0xDA
                break
            if marker in {0x01, 0xD8} or 0xD0 <= marker <= 0xD7:
                continue
            if offset + 2 > len(data):
                raise ValidationError("truncated JPEG segment")
            size = struct.unpack(">H", data[offset:offset + 2])[0]
            if size < 2 or offset + size > len(data):
                raise ValidationError("invalid JPEG segment length")
            if marker in sof:
                if size < 8:
                    raise ValidationError("truncated JPEG SOF")
                height, width = struct.unpack(">HH", data[offset + 3:offset + 7])
                dimensions = width, height
            offset += size
        if dimensions is None or not has_scan or b"\xff\xd9" not in data[-32:]:
            raise ValidationError("JPEG missing dimensions, image scan or end marker")
        result = {"format": "jpeg", "content_type": "image/jpeg", "width": dimensions[0], "height": dimensions[1]}
    elif len(data) >= 20 and data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        limit = struct.unpack("<I", data[4:8])[0] + 8
        if limit > len(data) or limit < 20:
            raise ValidationError("invalid WebP RIFF size")
        offset = 12
        dimensions = None
        while offset + 8 <= limit:
            tag = data[offset:offset + 4]
            size = struct.unpack("<I", data[offset + 4:offset + 8])[0]
            start = offset + 8
            if start + size > limit:
                raise ValidationError("truncated WebP chunk")
            chunk = data[start:start + size]
            if tag == b"VP8X" and size >= 10:
                dimensions = 1 + int.from_bytes(chunk[4:7], "little"), 1 + int.from_bytes(chunk[7:10], "little")
            elif tag == b"VP8 " and size >= 10 and chunk[3:6] == b"\x9d\x01\x2a":
                dimensions = struct.unpack("<H", chunk[6:8])[0] & 0x3FFF, struct.unpack("<H", chunk[8:10])[0] & 0x3FFF
            elif tag == b"VP8L" and size >= 5 and chunk[0] == 0x2F:
                bits = int.from_bytes(chunk[1:5], "little")
                dimensions = 1 + (bits & 0x3FFF), 1 + ((bits >> 14) & 0x3FFF)
            if dimensions is not None:
                break
            offset = start + size + (size & 1)
        if dimensions is None:
            raise ValidationError("WebP missing dimensions")
        result = {"format": "webp", "content_type": "image/webp", "width": dimensions[0], "height": dimensions[1]}
    else:
        raise ValidationError("response is not PNG, JPEG or WebP")
    if result["width"] <= 0 or result["height"] <= 0:
        raise ValidationError("image has zero pixel dimension")
    return result


class AttachmentParser(HTMLParser):
    """Find the representation image belonging to one actual new message."""
    VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"}

    def __init__(self, client_message_id):
        super().__init__(convert_charrefs=True)
        self.message_id = "message_" + client_message_id
        self.stack = []
        self.candidates = []

    def handle_starttag(self, tag, attrs):
        attributes = dict(attrs)
        inside = any(entry[1] for entry in self.stack) or attributes.get("id") == self.message_id
        if tag == "img":
            src = attributes.get("src") or ""
            classes = (attributes.get("class") or "").split()
            path = urllib.parse.urlsplit(src).path
            if "avatar" not in classes and "/avatar" not in path and "/rails/active_storage/representations/" in path and "message__attachment" in classes:
                self.candidates.append({"src": src, "in_new_message": inside, "attributes": attributes})
        if tag not in self.VOID:
            self.stack.append((tag, inside))

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)
        if tag not in self.VOID:
            self.handle_endtag(tag)

    def handle_endtag(self, tag):
        for position in range(len(self.stack) - 1, -1, -1):
            if self.stack[position][0] == tag:
                del self.stack[position:]
                break

    def attachment(self):
        selected = [candidate for candidate in self.candidates if candidate["in_new_message"]]
        if len(selected) != 1:
            raise ValidationError(f"expected one Active Storage image in {self.message_id}; found {len(selected)} (all representation images: {len(self.candidates)})")
        return selected[0]


def origin(url):
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme not in {"http", "https"} or not parsed.hostname or parsed.username or parsed.password:
        raise ValidationError("invalid HTTP URL")
    return parsed.scheme.lower(), parsed.hostname.lower(), parsed.port or (443 if parsed.scheme == "https" else 80)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Client:
    def __init__(self, base, cookie, timeout, out_dir):
        self.base = base.rstrip("/")
        self.allowed_origin = origin(base)
        self.cookie = cookie
        self.timeout = timeout
        self.out_dir = pathlib.Path(out_dir) if out_dir else None
        if self.out_dir:
            self.out_dir.mkdir(parents=True, exist_ok=True)
        # No environment proxy or implicit redirect can change the destination.
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())

    def request(self, method, url, headers, body, label):
        url = urllib.parse.urljoin(self.base + "/", url)
        if origin(url) != self.allowed_origin:
            raise ValidationError("refusing cross-origin benchmark request: " + url)
        outgoing = {"Cookie": self.cookie, "Accept-Encoding": "identity", **headers}
        request = urllib.request.Request(url, data=body, headers=outgoing, method=method)
        started = time.perf_counter_ns()
        try:
            response = self.opener.open(request, timeout=self.timeout)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            payload = response.read()
            elapsed = (time.perf_counter_ns() - started) / 1_000_000
            status = response.status
            response_headers = list(response.headers.items())
        evidence = f"HTTP {status}\n".encode() + b"".join(f"{key}: {value}\n".encode() for key, value in response_headers) + b"\n" + payload
        record = {"method": method, "url": url, "status": status, "ms": elapsed, "headers": response_headers, "bytes": len(payload), "body_sha256": hashlib.sha256(payload).hexdigest(), "response_sha256": hashlib.sha256(evidence).hexdigest()}
        if self.out_dir:
            path = self.out_dir / (label + ".response")
            path.write_bytes(evidence)
            record["response_file"] = str(path)
        return record, payload


def multipart(csrf, client_message_id, filename, content_type, data):
    boundary = "----bench" + secrets.token_hex(16)
    filename = filename.replace("\\", "\\\\").replace('"', '\\"').replace("\r", "").replace("\n", "")
    sections = []
    for key, value in [("authenticity_token", csrf), ("message[client_message_id]", client_message_id)]:
        sections.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{key}"\r\n\r\n{value}\r\n'.encode())
    sections += [f'--{boundary}\r\nContent-Disposition: form-data; name="message[attachment]"; filename="{filename}"\r\nContent-Type: {content_type}\r\n\r\n'.encode(), data, f"\r\n--{boundary}--\r\n".encode()]
    return boundary, b"".join(sections)


def run(args):
    file = pathlib.Path(args.file)
    data = file.read_bytes()
    source = image_info(data)
    client = Client(args.base, args.cookie, args.timeout, args.out_dir)
    runs = []
    for repetition in range(args.reps):
        client_message_id = "upload-bench-" + secrets.token_hex(16)
        boundary, body = multipart(args.csrf, client_message_id, file.name, source["content_type"], data)
        sample = {"repetition": repetition + 1, "client_message_id": client_message_id, "request_bytes": len(body), "responses": []}
        runs.append(sample)
        started = time.perf_counter_ns()
        try:
            post, html_bytes = client.request("POST", f"/rooms/{args.room}/messages", {"Content-Type": "multipart/form-data; boundary=" + boundary, "Accept": "text/vnd.turbo-stream.html, text/html", "X-CSRF-Token": args.csrf, "Sec-Fetch-Site": "same-origin"}, body, f"run-{repetition + 1:02}-post")
            sample["responses"].append(post)
            sample.update(post_status=post["status"], post_ms=post["ms"], post_html=html_bytes.decode("utf-8", errors="replace"))
            if not 200 <= post["status"] < 300:
                raise ValidationError(f"message POST returned {post['status']}")
            parser = AttachmentParser(client_message_id)
            parser.feed(sample["post_html"])
            attached = parser.attachment()
            sample["attachment"] = attached
            url = urllib.parse.urljoin(post["url"], attached["src"])
            sample["thumbnail_url"] = url
            fetch_started = time.perf_counter_ns()
            image = b""
            for hop in range(11):
                thumbnail, image = client.request("GET", url, {}, None, f"run-{repetition + 1:02}-thumb-{hop:02}")
                sample["responses"].append(thumbnail)
                if 300 <= thumbnail["status"] < 400:
                    location = next((value for key, value in thumbnail["headers"] if key.lower() == "location"), None)
                    if not location:
                        raise ValidationError("thumbnail redirect missing Location")
                    url = urllib.parse.urljoin(url, location)
                    continue
                break
            else:
                raise ValidationError("thumbnail exceeded 10 redirects")
            sample.update(thumb_status=thumbnail["status"], thumb_bytes=len(image), thumb_ms=(time.perf_counter_ns() - fetch_started) / 1_000_000, final_url=url)
            if thumbnail["status"] != 200:
                raise ValidationError(f"thumbnail GET returned {thumbnail['status']}")
            encoding = next((value for key, value in thumbnail["headers"] if key.lower() == "content-encoding"), "identity")
            if encoding.lower() != "identity":
                raise ValidationError("unexpected encoded thumbnail response: " + encoding)
            actual = image_info(image)
            declared = next((value.split(";")[0].strip().lower() for key, value in thumbnail["headers"] if key.lower() == "content-type"), "")
            if declared != actual["content_type"]:
                raise ValidationError(f"image Content-Type {declared!r} disagrees with magic {actual['content_type']}")
            scale = min(1, 1200 / source["width"], 800 / source["height"])
            expected = source["width"] * scale, source["height"] * scale
            if abs(actual["width"] - expected[0]) > 1 or abs(actual["height"] - expected[1]) > 1:
                raise ValidationError(f"thumbnail dimensions {actual['width']}x{actual['height']} differ from resize_to_limit expected {expected[0]:.3f}x{expected[1]:.3f}")
            sample["image"] = actual
            sample["image_sha256"] = hashlib.sha256(image).hexdigest()
            sample["success"] = True
        except (ValidationError, OSError, urllib.error.URLError, TimeoutError) as error:
            sample["success"] = False
            sample["error"] = str(error)
        finally:
            sample["total_ms"] = (time.perf_counter_ns() - started) / 1_000_000
    totals = [sample["total_ms"] for sample in runs if sample["success"]]
    return {"file": file.name, "bytes": len(data), "source_image": source, "source_sha256": hashlib.sha256(data).hexdigest(), "base": args.base, "room": args.room, "repetitions": args.reps, "success_count": len(totals), "error_count": len(runs) - len(totals), "median_total_ms": statistics.median(totals) if totals else None, "runs": runs}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True)
    parser.add_argument("--cookie", required=True)
    parser.add_argument("--room", required=True)
    parser.add_argument("--csrf", required=True)
    parser.add_argument("--file", required=True)
    parser.add_argument("--reps", type=int, default=5)
    parser.add_argument("--timeout", type=float, default=60)
    parser.add_argument("--out-dir", help="Optional raw status/headers/body capture directory")
    args = parser.parse_args()
    if args.reps < 1 or args.timeout <= 0 or not args.room.isdecimal():
        parser.error("reps/timeout must be positive and room must be numeric")
    try:
        result = run(args)
    except (ValidationError, OSError) as error:
        print(json.dumps({"error": str(error), "error_count": 1, "success_count": 0}))
        return 1
    print(json.dumps(result, separators=(",", ":")))
    return 1 if result["error_count"] else 0


if __name__ == "__main__":
    sys.exit(main())
