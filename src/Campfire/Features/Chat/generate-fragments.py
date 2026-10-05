from pathlib import Path
import re
root=Path(__file__).resolve().parents[4]
names=['composer','optimistic','lightbox','direct-new']
source='''// Static markup translated from the pinned Rails/Go templates. Dynamic values are encoded by ChatRenderer.
namespace Campfire.Features.Chat;

internal static class HtmlFragments
{
'''
for name in names:
    file='room-form.html' if name=='direct-new' else name+'.html'
    text=(root/'reference-go/internal/web/templates'/file).read_text(encoding='utf-8')
    text=re.search(r'{{define "'+name+r'"}}([\s\S]*?){{end}}',text)[1]
    text=re.sub(r'{{asset "([^"]+)"}}',r'%%ASSET:\1%%',text)
    tokens={'.Room.ID':'ROOM_ID','.User.ID':'USER_ID','.User.Title':'USER_TITLE','.User.Name':'USER_NAME','avatar .User.ID .User.UpdatedAt':'USER_AVATAR'}
    text=re.sub(r'{{([^}]+)}}',lambda m:'%%'+tokens[m[1]]+'%%',text)
    source+='    public const string '+name.title().replace('-','')+' = """\n'+text+'\n""";\n'
source+='}\n'
(root/'src/Campfire/Features/Chat/HtmlFragments.cs').write_text(source,encoding='utf-8')
sound_source='namespace Campfire.Features.Chat;\ninternal static class SoundCatalog\n{\n    internal sealed record Sound(string? Text, string? Image, int Width, int Height);\n    public static readonly Dictionary<string, Sound> Values = new()\n    {\n'
import json
for line in (root/'upstream/app/models/sound.rb').read_text(encoding='utf-8').splitlines():
    match=re.search(r'new\(name: "([^"]+)", (.*)\)',line)
    if not match:continue
    name, tail=match.groups()
    text=re.search(r'text: "([^"]*)"',tail)
    image=re.search(r'image: \{ name: "([^"]+)", width: (\d+), height: (\d+)',tail)
    if image: value='new(null, '+json.dumps(image[1],ensure_ascii=False)+', '+image[2]+', '+image[3]+')'
    elif text:value='new('+json.dumps(text[1],ensure_ascii=False)+', null, 0, 0)'
    else:continue
    sound_source+='        ['+json.dumps(name)+'] = '+value+',\n'
sound_source+='    };\n}\n'
(root/'src/Campfire/Features/Chat/SoundCatalog.cs').write_text(sound_source,encoding='utf-8')
