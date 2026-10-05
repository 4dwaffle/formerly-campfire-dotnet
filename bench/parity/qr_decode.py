"""Decode real Rails/JIT/AOT QR SVG responses with an independent ZXing reader.

Install zxing-cpp into bench/.work/qr-deps; Pillow is supplied by the workspace.
The rasterizer accepts the rectangular module shapes emitted by both encoders,
and rejects other SVG operations rather than silently ignoring them.
"""
import argparse
import base64
import hashlib
import json
import pathlib
import re
import sys
import urllib.request
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'bench/.work/qr-deps'))
import zxingcpp
from PIL import Image, ImageDraw


def rasterize(svg):
    root = ET.fromstring(svg)
    _, _, width, height = map(float, root.attrib['viewBox'].split())
    scale = max(1, int(400 / width))
    padding = 48
    image = Image.new('L', (int(width * scale) + padding * 2, int(height * scale) + padding * 2), 255)
    draw = ImageDraw.Draw(image)
    def rectangle(x, y, w, h, fill):
        draw.rectangle((int(x * scale) + padding, int(y * scale) + padding,
                        int((x + w) * scale) + padding - 1, int((y + h) * scale) + padding - 1), fill=fill)
    for element in root:
        tag = element.tag.rsplit('}', 1)[-1]
        fill = 255 if element.attrib.get('fill', '').lower() in ('white', '#ffffff') else 0
        if tag == 'rect':
            rectangle(float(element.attrib.get('x', 0)), float(element.attrib.get('y', 0)), float(element.attrib['width']), float(element.attrib['height']), fill)
        elif tag == 'path':
            path = element.attrib['d']
            pattern = r'M([0-9.]+) ([0-9.]+)h([0-9.]+)v([0-9.]+)h-([0-9.]+)z'
            if re.sub(pattern, '', path).strip():
                raise ValueError('Unsupported SVG path: QR capture cannot be certified')
            for x, y, w, h, closing in re.findall(pattern, path):
                if w != closing: raise ValueError('Nonrectangular QR module')
                rectangle(float(x), float(y), float(w), float(h), fill)
        else:
            raise ValueError('Unsupported SVG element: ' + tag)
    return image


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=pathlib.Path, required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    instances = json.loads(args.manifest.read_text())['instances']
    values = ['https://example.com/join/fixture', 'https://example.com/join/' + 'a' * 300, 'https://example.com/join/Žluťoučký-🔥']
    results = []
    for name, instance in instances.items():
        for value in values:
            segment = base64.urlsafe_b64encode(value.encode()).decode()
            request = urllib.request.Request(instance['base'] + '/qr_code/' + segment, headers={'User-Agent': 'Mozilla/5.0 Chrome/131.0.0.0 Safari/537.36'})
            with urllib.request.urlopen(request) as response:
                svg = response.read()
                code = zxingcpp.read_barcode(rasterize(svg))
                results.append({'application': name, 'input': value, 'status': response.status,
                                'svg_sha256': hashlib.sha256(svg).hexdigest(),
                                'decoded': code.text if code else None,
                                'matches': code is not None and code.text == value})
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(results, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    print(json.dumps({'decoded': len(results), 'passed': sum(r['matches'] for r in results)}))
    return 0 if all(r['matches'] for r in results) else 1


if __name__ == '__main__': raise SystemExit(main())
