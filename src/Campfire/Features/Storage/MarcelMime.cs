using System.Text.Json;
using System.Text.RegularExpressions;

namespace Campfire.Features.Storage;

// Marcel1.1.0's ordered magic matcher and subtype selection. Tables are
// exported from the pinned dependency, including its custom definitions.
public static class MarcelMime
{
    private sealed record Rule(int Start,int End,byte[]? Bytes,Rule[]? Children);
    private static readonly Dictionary<string,string> Extensions;
    private static readonly Dictionary<string,string[]> Parents;
    private static readonly (string Type,Rule[] Rules)[] Magic;
    static MarcelMime()
    {
        using var data=JsonDocument.Parse(MarcelTables.Json);
        Extensions=data.RootElement.GetProperty("extensions").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetString()!);
        Parents=data.RootElement.GetProperty("parents").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.EnumerateArray().Select(x=>x.GetString()!).ToArray());
        static Rule[] Rules(JsonElement array)=>array.EnumerateArray().Select(entry=>new Rule(entry[0].GetInt32(),entry[1].GetInt32(),entry[2].ValueKind==JsonValueKind.Null?null:Convert.FromBase64String(entry[2].GetString()!),entry[3].ValueKind==JsonValueKind.Null?null:Rules(entry[3]))).ToArray();
        Magic=data.RootElement.GetProperty("magic").EnumerateArray().Select(entry=>(entry[0].GetString()!,Rules(entry[1]))).ToArray();
    }
    public static string? ForExtension(string filename)=>Extensions.GetValueOrDefault(Path.GetExtension(filename).TrimStart('.').ToLowerInvariant());
    public static bool Child(string child,string parent)=>child==parent||Parents.TryGetValue(child,out var parents)&&parents.Any(candidate=>Child(candidate,parent));
    public static string Identify(string path,string? filename,string? declared)
    {
        using var stream=File.OpenRead(path);
        bool Matches(Rule[] rules)
        {
            foreach(var rule in rules)
            {
                if(rule.Bytes is null||rule.Start>stream.Length)continue;
                var count=(int)Math.Min(stream.Length-rule.Start,(long)rule.End-rule.Start+rule.Bytes.Length);
                if(count<rule.Bytes.Length)continue;
                var buffer=new byte[count];stream.Position=rule.Start;stream.ReadExactly(buffer);
                var found=rule.Start==rule.End?buffer.AsSpan().SequenceEqual(rule.Bytes):buffer.AsSpan().IndexOf(rule.Bytes)>=0;
                if(found&&(rule.Children is null||Matches(rule.Children)))return true;
            }
            return false;
        }
        var detected=Magic.FirstOrDefault(entry=>Matches(entry.Rules)).Type;
        var declaration=declared is null?null:Regex.Split(declared.ToLowerInvariant(),"[;,\\s]",RegexOptions.CultureInvariant)[0];
        if(declaration=="application/octet-stream"||declaration?.Contains('/')!=true)declaration=null;
        var named=filename is null?null:ForExtension(filename);
        var selected=detected;
        foreach(var candidate in new[]{declaration,named,"application/octet-stream"})if(candidate is not null&&(selected is null||Child(candidate,selected)))selected=candidate;
        return selected!;
    }
}
