"""Generate the exact pinned Ruby Unicode emoji character set, without network."""
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[3]
IMAGE = 'ghcr.io/basecamp/once-campfire@sha256:7197fff46e15d0dce69e0a16624df239e1b9bd069534dc28dec67ab7b6771672'
RUBY = r'''require "json"; ranges=[]; first=nil; last=nil; (0..0x10ffff).each { |n| next if (0xd800..0xdfff).cover?(n); matches=n.chr(Encoding::UTF_8).match?(/\A(\p{Emoji_Presentation}|\p{Extended_Pictographic}|\uFE0F)\z/u); if matches; if first.nil?; first=last=n; elsif n==last+1; last=n; else; ranges << [first,last]; first=last=n; end; elsif !first.nil?; ranges << [first,last]; first=last=nil; end }; ranges << [first,last] unless first.nil?; puts JSON.generate(ranges)'''
result = subprocess.run(['docker', 'run', '--rm', '--network', 'none', '--entrypoint', 'ruby', IMAGE, '-e', RUBY], check=True, capture_output=True, text=True)
ranges = json.loads(result.stdout)
lines = ',\n'.join(f'        (0x{start:X}, 0x{end:X})' for start, end in ranges)
source = '''// Generated using pinned Ruby's Emoji_Presentation/Extended_Pictographic properties.
// Reproduce with bench/parity/chat/generate_emoji.py; upstream/lib/rails_ext/string.rb.
namespace Campfire.Features.Chat;

public static class EmojiCharacters
{
    private static readonly (int Start, int End)[] Ranges =
    [
''' + lines + '''
    ];
    public static bool IsEmoji(int value)
    {
        var low = 0; var high = Ranges.Length - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var range = Ranges[mid];
            if (value < range.Start) high = mid - 1;
            else if (value > range.End) low = mid + 1;
            else return true;
        }
        return false;
    }
}
'''
(ROOT / 'src/Campfire/Features/Chat/EmojiCharacters.cs').write_text(source, encoding='utf-8')
print(json.dumps({'ranges': len(ranges), 'codepoints': sum(end - start + 1 for start, end in ranges)}))
