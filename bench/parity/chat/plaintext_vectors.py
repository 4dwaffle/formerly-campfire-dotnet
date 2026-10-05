"""Capture plaintext expectations from the exact pinned Action Text converter."""
import argparse
import json
import subprocess
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--instances', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--content', action='store_true', help='Capture the public ActionText::Content pipeline, including Fragment.from_html stripping.')
    args = parser.parse_args()
    container = json.loads(args.instances.read_text())['instances']['rails']['container']
    cases = ['<p>First</p><p>Second</p>', '<ul><li>one</li><li>two<ul><li>nested</li></ul></li></ul>', '<ol start="5"><li>one</li><li>two</li></ol>', '<blockquote> quoted </blockquote>', '<figure><figcaption>caption</figcaption></figure>', '<h2>head</h2><pre>a\nb</pre>', ' spaces ', '<custom>preserved<script>removed</script></custom>', '<div>a<br>b</div><div>c</div>', '<table><tr><td>one</td><td>two</td></tr><tr><td>three</td></tr></table>', '<blockquote></blockquote>', '<p>one<br><br></p><p>two</p>', '<ul><li>a<ol><li>b<ul><li>c</li></ul></li></ol></li></ul>']
    ruby = "cases=JSON.parse(STDIN.read); puts JSON.generate(cases.map { |html| {html: html,expected: ActionText::PlainTextConversion.node_to_plain_text(Nokogiri::HTML.fragment(html))}})"
    command = ['docker', 'exec', '-i', container, 'bundle', 'exec', 'ruby', '-rjson', '-rnokogiri', '-ractive_support/core_ext/object/blank', '-r/usr/local/bundle/ruby/3.4.0/bundler/gems/rails-1a02651ac37f/actiontext/lib/action_text/plain_text_conversion', '-e', ruby]
    payload = json.dumps(cases)
    if args.content:
        command = ['docker', 'exec', '-i', container, 'bundle', 'exec', 'rails', 'runner', '-']
        payload = 'cases=JSON.parse(' + json.dumps(json.dumps(cases)) + '); puts JSON.generate(cases.map { |html| {html: html,expected: ActionText::Content.new(html).to_plain_text}})'
    result = subprocess.run(command, input=payload, text=True, encoding='utf-8', capture_output=True, check=True)
    vectors = json.loads(result.stdout)
    args.output.write_text(json.dumps(vectors, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    print(result.stdout)


if __name__ == '__main__':
    main()
