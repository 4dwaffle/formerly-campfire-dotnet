"""Compile the three pinned, simple PWA ERB partials into owned C# markup."""
from pathlib import Path
import json
import re

ROOT = Path(__file__).resolve().parents[3]


def quote(value):
    return json.dumps(value, ensure_ascii=True)


def expression(value):
    value = value.strip()
    if value.startswith('image_tag '):
        name = re.search(r'"([^"]+)"', value).group(1)
        size = re.search(r'size: (\d+)', value)
        alt = re.search(r'alt: "([^"]*)"', value)
        cls = re.search(r'class: "([^"]*)"', value)
        return f'Image(renderer,{quote(name)},{size.group(1) if size else 20},{quote(alt.group(1)) if alt else "null"},{quote(cls.group(1)) if cls else "null"})'
    if value == 'platform.browser.capitalize':
        return 'E(platform.Browser)'
    if value == 'platform.operating_system':
        return 'E(platform.OperatingSystem)'
    if value == 'root_url':
        return 'E(context.Request.Scheme + "://" + context.Request.Host + context.Request.PathBase + "/")'
    raise ValueError(value)


def condition(value):
    return re.sub(r'platform\.(\w+)\?', lambda m: 'platform.' + m[1].capitalize(), value.strip())


def compile_method(method, path):
    result = [f'    public static string {method}(HttpContext context, IPageRenderer renderer)', '    {', '        var platform = new Platform(context.Request.Headers.UserAgent.ToString());', '        var html = new StringBuilder();']
    first_case = []
    for part in re.split(r'(<%.*?%>)', path.read_text(encoding='utf-8'), flags=re.S):
        if not part:
            continue
        if not part.startswith('<%'):
            result.append('        html.Append(' + quote(part) + ');')
            continue
        if part.startswith('<%='):
            result.append('        html.Append(' + expression(part[3:-2]) + ');')
            continue
        command = part[2:-2].strip()
        if command.startswith('unless '):
            result.extend(['        if (!(' + condition(command[7:]) + '))', '        {'])
        elif command.startswith('if '):
            result.extend(['        if (' + condition(command[3:]) + ')', '        {'])
        elif command.startswith('case'):
            value = command.split('when', 1)[1]
            result.extend(['        if (' + condition(value) + ')', '        {'])
        elif command.startswith('when '):
            result.extend(['        }', '        else if (' + condition(command[5:]) + ')', '        {'])
        elif command == 'else':
            result.extend(['        }', '        else', '        {'])
        elif command == 'end':
            result.append('        }')
        else:
            raise ValueError(command)
    result.extend(['        return html.ToString();', '    }'])
    return '\n'.join(result)


prefix = '''// Generated from pinned upstream/app/views/pwa; regenerate with bench/parity/chat/generate_pwa.py.
using System.Net;
using System.Text;
using Campfire.Contracts;

namespace Campfire.Features.Chat;

public static class PwaInstructions
{
    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static string Image(IPageRenderer renderer, string logical, int size, string? alt, string? css) => $"<img src=\\"{E(renderer.Asset(logical))}\\" width=\\"{size}\\" height=\\"{size}\\"{(alt is null ? " aria-hidden=\\"true\\"" : " alt=\\"" + E(alt) + "\\"")}{(css is null ? "" : " class=\\"" + E(css) + "\\"")}>";
    private sealed class Platform
    {
        public Platform(string ua)
        {
            Ios = ua.Contains("iPhone") || ua.Contains("iPad") || ua.Contains("iPod") || ua.Contains("Macintosh") && ua.Contains("Mobile");
            Android = ua.Contains("Android"); Windows = ua.Contains("Windows");
            Edge = ua.Contains("Edg/") || ua.Contains("EdgA/") || ua.Contains("EdgiOS/");
            Firefox = ua.Contains("Firefox/") || ua.Contains("FxiOS/");
            Chrome = !Edge && (ua.Contains("Chrome/") || ua.Contains("CriOS/"));
            Safari = !Edge && !Firefox && !Chrome && ua.Contains("Safari/");
            Desktop = !Ios && !Android;
            Browser = Edge ? "Edge" : Firefox ? "Firefox" : Chrome ? "Chrome" : Safari ? "Safari" : "Unknown";
            OperatingSystem = Ios ? "iOS" : Android ? "Android" : Windows ? "Windows" : ua.Contains("Mac") ? "macOS" : ua.Contains("Linux") ? "Linux" : "Unknown";
        }
        public bool Ios { get; } public bool Android { get; } public bool Windows { get; } public bool Edge { get; }
        public bool Firefox { get; } public bool Chrome { get; } public bool Safari { get; } public bool Desktop { get; }
        public string Browser { get; } public string OperatingSystem { get; }
    }
'''
methods = [compile_method(method, ROOT / 'upstream/app/views/pwa' / ('_' + file + '.html.erb')) for method, file in [('Browser', 'browser_settings'), ('System', 'system_settings'), ('Install', 'install_instructions')]]
(ROOT / 'src/Campfire/Features/Chat/PwaInstructions.cs').write_text(prefix + '\n\n'.join(methods) + '\n}\n', encoding='utf-8')
