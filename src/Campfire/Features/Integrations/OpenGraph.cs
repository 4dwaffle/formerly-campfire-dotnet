using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Campfire.Contracts;

namespace Campfire.Features.Integrations;

public sealed record PublicDocument(string? Body, string? ContentType);
public interface IPublicDocumentClient
{
    Task<PublicDocument?> FetchAsync(Uri uri, bool head, CancellationToken cancellationToken);
    Task<bool> PublicAsync(Uri uri, CancellationToken cancellationToken);
}

public sealed class PublicDocumentClient : IPublicDocumentClient
{
    public static bool ValidUri(Uri? uri) => uri is not null && uri.IsAbsoluteUri && uri.Scheme is "http" or "https" && uri.Host.Length > 0;
    private static async Task<IPAddress?> Resolve(Uri uri, CancellationToken cancellationToken)
    {
        if (!ValidUri(uri)) return null;
        try { var addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, cancellationToken); return addresses.FirstOrDefault(OutboundPolicy.PublicAddress); }
        catch (System.Net.Sockets.SocketException) { return null; }
    }
    public async Task<bool> PublicAsync(Uri uri, CancellationToken cancellationToken) => await Resolve(uri, cancellationToken) is not null;
    public async Task<PublicDocument?> FetchAsync(Uri uri, bool head, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            for (var redirects = 0; redirects < 10; redirects++)
            {
                var address = await Resolve(uri, budget.Token); if (address is null) return null;
                // DNS is revalidated on every redirect and the actual socket uses the guarded
                // address. Environment proxies cannot re-resolve or bypass this policy.
                using var handler = OutboundPolicy.PinnedHandler(address);handler.ConnectTimeout=TimeSpan.FromSeconds(60);using var http = new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
                using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, uri);
                if(uri.UserInfo.Length>0)request.Headers.TryAddWithoutValidation("Authorization","Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(uri.UserInfo))));
                using var headersTimeout=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);headersTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    // Rails refuses relative redirects in its explicit URL guard.
                    if (location is null || !ValidUri(location)) return null;
                    uri = location; continue;
                }
                var type = response.Content.Headers.ContentType?.MediaType;
                if (head) return new(null, response.Content.Headers.ContentType?.ToString());
                if (response.StatusCode != HttpStatusCode.OK || type != "text/html" || response.Content.Headers.ContentLength is > 5 * 1024 * 1024) return null;
                await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
                using var bytes = new MemoryStream(); var buffer = new byte[8192]; int read;
                while(true){using var readTimeout=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);readTimeout.CancelAfter(TimeSpan.FromSeconds(60));read=await stream.ReadAsync(buffer,readTimeout.Token);if(read==0)break;if(bytes.Length+read>5*1024*1024)return null;bytes.Write(buffer,0,read);}
                // Nokogiri receives Net::HTTP's binary body and follows HTML's
                // meta encoding, rather than the HTTP Content-Type charset.
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var encoding=Encoding.Latin1;
                var metaCharset=Regex.Match(Encoding.ASCII.GetString(bytes.ToArray()),"<meta\\b[^>]*charset\\s*=\\s*[\"']?([^\\s\"'/>;]+)",RegexOptions.IgnoreCase).Groups[1].Value;
                try{if(metaCharset.Length>0)encoding=Encoding.GetEncoding(metaCharset);}catch(ArgumentException){ }
                return new(encoding.GetString(bytes.ToArray()), type);
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ArgumentException) { }
        return null;
    }
}

public sealed class OpenGraphService(IPublicDocumentClient documents)
{
    private static readonly Regex MediaPath = new(@"\bhttps?://\S+\.(?:zip|tar|tar\.gz|tar\.bz2|tar\.xz|gz|bz2|rar|7z|dmg|exe|msi|pkg|deb|iso|jpg|jpeg|png|gif|bmp|mp4|mov|avi|mkv|wmv|flv|heic|heif|mp3|wav|ogg|aac|wma|webm|ogv|mpg|mpeg)\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public async Task<IResult> Create(HttpContext context)
    {
        if (context.User() is null) return Results.Redirect("/session/new");
        var auth = context.RequestServices.GetRequiredService<IAuthService>();
        string? formToken = null; string? url;
        try
        {
            if (context.Request.HasFormContentType) { var form = await context.Request.ReadFormAsync(); url = form["url"].ToString(); formToken = form["authenticity_token"]; }
            else { using var doc = await JsonDocument.ParseAsync(context.Request.Body); url = doc.RootElement.TryGetProperty("url", out var value) ? value.GetString() : null; }
        }
        catch (JsonException) { return Results.BadRequest(); }
        if (!auth.ValidateCsrf(context, formToken)) return Results.StatusCode(422);
        if (string.IsNullOrWhiteSpace(url)) return Results.BadRequest();
        var result = await FromUrl(url, context.RequestAborted);
        return result is null ? Results.NoContent() : Results.Json(result, IntegrationsJson.Default.OpenGraphMetadata);
    }
    public async Task<OpenGraphMetadata?> FromUrl(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !PublicDocumentClient.ValidUri(uri) || MediaPath.IsMatch(url)) return null;
        if (uri.Host is "twitter.com" or "www.twitter.com" or "x.com" or "www.x.com" && uri.AbsolutePath != "/") uri = new UriBuilder(uri) { Host = "fxtwitter.com" }.Uri;
        var response = await documents.FetchAsync(uri, false, cancellationToken);
        if (response?.Body is null) return null;
        var values = Parse(response.Body);
        var title = StripMarkup(values.GetValueOrDefault("title") ?? "");
        var description = StripMarkup(values.GetValueOrDefault("description") ?? "");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description)) return null;
        var canonical = url;
        if (Uri.TryCreate(values.GetValueOrDefault("url"), UriKind.Absolute, out var preferred) && PublicDocumentClient.ValidUri(preferred) && await documents.PublicAsync(preferred, cancellationToken)) canonical = values["url"];
        string? image = null;
        if (Uri.TryCreate(values.GetValueOrDefault("image"), UriKind.Absolute, out var imageUri) && PublicDocumentClient.ValidUri(imageUri))
        {
            var imageResponse = await documents.FetchAsync(imageUri, true, cancellationToken);
            if (imageResponse?.ContentType?.ToLowerInvariant() is "image/png" or "image/jpeg" or "image/gif" or "image/webp") image = values["image"];
        }
        return new(title, canonical, image, description);
    }
    public static Dictionary<string, string> Parse(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        var hasEncoding=document.QuerySelectorAll("meta").Any(m=>m.HasAttribute("charset")||m.GetAttribute("http-equiv")?.Equals("Content-Type",StringComparison.OrdinalIgnoreCase)==true&&Regex.IsMatch(m.GetAttribute("content")??"","charset=",RegexOptions.IgnoreCase));
        var values = new Dictionary<string, string>();
        foreach (var meta in document.QuerySelectorAll("meta"))
        {
            var name = meta.GetAttribute("property") ?? meta.GetAttribute("name"); var content = meta.GetAttribute("content");
            if(name is "og:title" or "og:url" or "og:image" or "og:description"&&!string.IsNullOrWhiteSpace(content))values[name[3..]]=hasEncoding?content:new string(content.Where(character=>character<=127).ToArray());
        }
        return values;
    }
    private static string StripMarkup(string text) => new HtmlParser().ParseDocument(text).Body?.TextContent ?? "";
}
public sealed record OpenGraphMetadata(string Title, string Url, string? Image, string Description);
