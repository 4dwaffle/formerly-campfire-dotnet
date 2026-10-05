"""Functional check against a fresh local Docker instance; no load measurements."""
import http.cookiejar
import re
import struct
import sys
import urllib.parse
import urllib.request
import zlib

origin = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5090"
client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

def request(path, body=None, content_type=None):
    headers = {"Content-Type": content_type} if content_type else {}
    with client.open(urllib.request.Request(origin + path, body, headers), timeout=30) as response:
        return response.read(), response.headers

def csrf(html):
    return re.search(rb'<meta name="csrf-token" content="([^"]+)"', html)[1].decode()

page, _ = request("/first_run")
fields = {"authenticity_token": csrf(page), "user[name]": "Container Check",
          "user[email_address]": "container@example.test", "user[password]": "container-test-password"}
request("/first_run", urllib.parse.urlencode(fields).encode(), "application/x-www-form-urlencoded")
profile, _ = request("/users/me/profile")

def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

# An opaque 768x768 PNG fixture exercises Campfire's real 512px WebP avatar variant.
png = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 768, 768, 8, 2, 0, 0, 0))
       + chunk(b"IDAT", zlib.compress((b"\0" + b"\x20\x70\xd0" * 768) * 768)) + chunk(b"IEND", b""))
boundary = "campfire-functional-check"
parts = []
for name, value in {"authenticity_token": csrf(profile), "_method": "patch"}.items():
    parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="user[avatar]"; filename="fixture.png"\r\nContent-Type: image/png\r\n\r\n'.encode() + png + b"\r\n")
parts.append(f"--{boundary}--\r\n".encode())
profile, _ = request("/users/me/profile", b"".join(parts), "multipart/form-data; boundary=" + boundary)
avatar = re.search(rb'src="(/users/[^" ]+/avatar[^" ]*)"', profile)[1].decode().replace("&amp;", "&")
image, headers = request(avatar)
assert headers.get_content_type() == "image/webp", "Avatar must use Campfire's WebP variant"
assert image[:4] == b"RIFF" and image[8:12] == b"WEBP", "Avatar did not return WebP"
if image[12:16] == b"VP8 ":
    assert image[23:26] == b"\x9d\x01\x2a", "Malformed WebP frame"
    dimensions = tuple(value & 0x3fff for value in struct.unpack("<HH", image[26:30]))
elif image[12:16] == b"VP8L":
    bits = int.from_bytes(image[21:25], "little")
    dimensions = ((bits & 0x3fff) + 1, ((bits >> 14) & 0x3fff) + 1)
else:
    assert image[12:16] == b"VP8X", "Unknown WebP variant"
    dimensions = (int.from_bytes(image[24:27], "little") + 1, int.from_bytes(image[27:30], "little") + 1)
assert dimensions == (512, 512), f"Avatar variant has wrong dimensions: {dimensions}"
print("PASS Docker: fresh setup, authenticated profile upload, real libvips avatar variant")
