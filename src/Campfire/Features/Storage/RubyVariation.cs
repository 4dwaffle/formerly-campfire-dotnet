using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Campfire.Features.Storage;

// Rails identifies tracked variants by SHA1(Marshal.dump(transformations)).
public static class RubyVariation
{
    public static string Digest(JsonElement transformations, bool symbolFormat = false)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 4, 8 });
        var symbols = new List<string>();
        void Number(int n)
        {
            if (n == 0) stream.WriteByte(0);
            else if (n > 0 && n < 123) stream.WriteByte((byte)(n + 5));
            else if(n<0&&n> -124)stream.WriteByte(unchecked((byte)(n-5)));
            else { var bytes = BitConverter.GetBytes(n); var count = 4; while (count > 1 && bytes[count - 1] == (n<0?255:0)) count--; stream.WriteByte(unchecked((byte)(n<0?-count:count))); stream.Write(bytes, 0, count); }
        }
        void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); Number(bytes.Length); stream.Write(bytes); }
        void Symbol(string value)
        {
            var index = symbols.IndexOf(value);
            if (index >= 0) { stream.WriteByte((byte)';'); Number(index); }
            else { symbols.Add(value); stream.WriteByte((byte)':'); Text(value); }
        }
        void Value(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    stream.WriteByte((byte)'{'); Number(value.EnumerateObject().Count());
                    foreach (var entry in value.EnumerateObject()) { Symbol(entry.Name); if (entry.Name == "format" && symbolFormat) Symbol(entry.Value.GetString()!); else Value(entry.Value); }
                    break;
                case JsonValueKind.Array:
                    stream.WriteByte((byte)'['); Number(value.GetArrayLength()); foreach (var child in value.EnumerateArray()) Value(child); break;
                case JsonValueKind.Number:
                    if(value.TryGetInt32(out var integer)){stream.WriteByte((byte)'i');Number(integer);}
                    else if(value.TryGetInt64(out var large)){stream.WriteByte((byte)'l');stream.WriteByte((byte)(large<0?'-':'+'));var magnitude=large<0?unchecked((ulong)-large):(ulong)large;var raw=BitConverter.GetBytes(magnitude);var count=8;while(count>2&&raw[count-1]==0&&raw[count-2]==0)count-=2;Number(count/2);stream.Write(raw,0,count);}
                    else {stream.WriteByte((byte)'f');Text(value.GetDouble().ToString("R",System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant());}break;
                case JsonValueKind.String:
                    stream.WriteByte((byte)'I'); stream.WriteByte((byte)'"'); Text(value.GetString()!); Number(1); Symbol("E"); stream.WriteByte((byte)'T'); break;
                case JsonValueKind.True:stream.WriteByte((byte)'T');break;
                case JsonValueKind.False:stream.WriteByte((byte)'F');break;
                case JsonValueKind.Null:stream.WriteByte((byte)'0');break;
                default: throw new ArgumentException("Unsupported transform type");
            }
        }
        Value(transformations);
        return Convert.ToBase64String(SHA1.HashData(stream.ToArray()));
    }
}
