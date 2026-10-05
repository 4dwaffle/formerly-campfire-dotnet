using System.Text.RegularExpressions;
namespace Campfire.Features.Integrations;

// Browser/version/platform display rules from useragent0.16.11, the pinned
// Campfire dependency. The OS/mobile/bot APIs are not used by this view.
public static class UserAgentLabel
{
    private sealed record Token(string Product,string Version,string[] Comments);
    public static string Format(string? text)
    {
        if(string.IsNullOrWhiteSpace(text))text="Mozilla/4.0 (compatible)";
        var tokens=new List<Token>();
        while(!string.IsNullOrEmpty(text))
        {
            var m=Regex.Match(text,"^[\"']*([^/\\s]+)/?([^\\s,]*)(?:\\s\\(([^)]*)\\)|,gzip\\(gfe\\))?",RegexOptions.CultureInvariant);
            if(!m.Success)break;tokens.Add(new(m.Groups[1].Value,m.Groups[2].Value,m.Groups[3].Success?m.Groups[3].Value.Split("; "):[]));text=text[m.Length..].Trim();
        }
        Token? Find(string name)=>tokens.FirstOrDefault(t=>t.Product.Equals(name,StringComparison.OrdinalIgnoreCase));
        var first=tokens.FirstOrDefault();var last=tokens.LastOrDefault();var commented=tokens.FirstOrDefault(t=>t.Comments.Length>0);
        var browser=first?.Product??"";var version=first?.Version??"";string? platform=null;
        var comments=first?.Comments??[];var comment0=comments.FirstOrDefault()??"";
        string? WebPlatform(bool chrome=false)=>commented is null?null:commented.Comments[0].Contains("Windows",StringComparison.Ordinal)?"Windows":chrome&&commented.Comments.Any(x=>x.Contains("CrOS",StringComparison.Ordinal))?"ChromeOS":commented.Comments.Any(x=>x.Contains("Android",StringComparison.Ordinal))?"Android":commented.Comments[0]=="BB10"?"BlackBerry":commented.Comments[0];
        if(last?.Product=="Edge"){browser="Edge";version=last.Version;platform="Windows";}
        else if(comments.Length>1&&(comments[1].Contains("MSIE",StringComparison.Ordinal)||Regex.IsMatch(string.Join("; ",comments),"Trident.+rv:"))){browser="Internet Explorer";version=Regex.Match(string.Join("; ",comments),"(?:MSIE\\s|rv:)([\\d.]+)").Groups[1].Value;platform="Windows";}
        else if(first?.Product=="Opera"||last?.Product=="OPR"){browser="Opera";version=Find("Version")?.Version??Find("OPR")?.Version??version;platform=comment0.Contains("Windows",StringComparison.Ordinal)?"Windows":comments.FirstOrDefault();var mini=Regex.Match(string.Join("; ",comments),"Opera Mini/([\\d.]+)");if(mini.Success)version=mini.Groups[1].Value;}
        else if(tokens.Any(t=>t.Product.Contains("MicroMessenger",StringComparison.OrdinalIgnoreCase))){browser="Wechat Browser";version=Find("MicroMessenger")?.Version??"";platform=comment0.Contains("iPhone",StringComparison.Ordinal)?"iPhone":comments.Any(x=>x.Contains("Android",StringComparison.Ordinal))?"Android":comments.FirstOrDefault();}
        else if(Find("Vivaldi") is not null){browser="Vivaldi";version=last?.Version??"";platform=WebPlatform(true);}
        else if(Find("Chrome") is not null||Find("CriOS") is not null){browser=Find("Iron") is not null?"Iron":"Chrome";version=Find("CriOS")?.Version??Find("Chrome")!.Version;platform=WebPlatform(true);}
        else if(Find("iTunes") is not null){browser="iTunes";version=Find("iTunes")!.Version;platform=WebPlatform();}
        else if(comment0.Contains("PLAYSTATION 3",StringComparison.Ordinal)||comment0.Contains("PlayStation Vita",StringComparison.Ordinal)||comment0.Contains("PlayStation 4",StringComparison.Ordinal)){platform=comment0.Contains("PLAYSTATION 3",StringComparison.Ordinal)?"PlayStation 3":comment0.Contains("PlayStation 4",StringComparison.Ordinal)?"PlayStation 4":"PlayStation Vita";browser=platform=="PlayStation 3"?"PS3 Internet Browser":last?.Product=="Silk"?"Silk":platform=="PlayStation 4"?"PS4 Internet Browser":"";version=last?.Product=="Silk"?last.Version:string.Join(' ',comments).Replace(platform=="PlayStation 3"?"PLAYSTATION 3 ":platform+" ","",StringComparison.Ordinal);}
        else if(tokens.Count>=3&&tokens[0].Product=="Podcast"&&tokens[1].Product=="Addict"&&tokens[2].Product=="-"){browser="Podcast Addict";version="";platform=tokens.Count>3&&tokens[3].Comments.Any(x=>x.Contains("Android",StringComparison.Ordinal))?"Android":null;}
        else if(Find("AppleWebKit") is not null||tokens.Any(t=>t.Comments.Any(c=>Regex.IsMatch(c,"^AppleWebKit/[\\d.]+",RegexOptions.IgnoreCase)))){platform=WebPlatform();browser=platform=="Android"?"Android":platform=="BlackBerry"?"BlackBerry":"Safari";version=Find("Version")?.Version??"";if(version.Length==0){var ios=Regex.Match(string.Join("; ",commented?.Comments??[]),"CPU (?:iPhone |iPod )?OS ([\\d_]+) like Mac OS X");if(ios.Success)version=ios.Groups[1].Value.Replace('_','.');else version=LegacySafari(Find("AppleWebKit")?.Version??"");}}
        else if(first?.Product=="Mozilla"){foreach(var name in new[]{"PaleMoon","Firefox","Camino","Iceweasel","Seamonkey"})if(Find(name) is Token found){browser=name;version=found.Version;break;}platform=comment0 is "compatible" or "Mobile"?null:comment0.StartsWith("Windows ",StringComparison.Ordinal)?"Windows":comments.FirstOrDefault();}
        else if(tokens.Any(t=>t.Product is "NSPlayer" or "Windows-Media-Player" or "WMFSDK")&&version is not ("4.1.0.3856" or "7.10.0.3059" or "7.0.0.1956")){browser="Windows Media Player";platform="Windows";}
        else if(Find("AppleCoreMedia") is not null){browser="AppleCoreMedia";platform=WebPlatform();}
        else if(Find("Lavf") is not null||Find("NSPlayer") is not null&&version=="4.1.0.3856"){browser="libavformat";version=Find("NSPlayer") is not null?"":version;}
        return $"{browser} {version} on {platform}";
    }
    private static string LegacySafari(string build)=>build switch{"85.7"=>"1.0","85.8.5"=>"1.0.3","85.8.2"=>"1.0.3","124"=>"1.2","125.2"=>"1.2.2","125.4"=>"1.2.3","125.5.5" or "125.5.6" or "125.5.7"=>"1.2.4","312.1.1" or "312.1"=>"1.3","312.5" or "312.5.1" or "312.5.2"=>"1.3.1","312.8" or "312.8.1"=>"1.3.2","412" or "412.6" or "412.6.2"=>"2.0","412.7"=>"2.0.1","416.11" or "416.12"=>"2.0.2","417.9" or "418"=>"2.0.3","418.8" or "418.9" or "418.9.1" or "419"=>"2.0.4","425.13"=>"2.2","534.52.7"=>"5.1.2",_=>""};
}
