using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Campfire.Contracts;
using Microsoft.AspNetCore.Http.Features;
using System.Buffers;
using Microsoft.AspNetCore.ResponseCompression;

namespace Campfire.Features.WebSupport;

public static class RailsHttpCompatibility
{
    public static async Task NormalizeRequest(HttpContext context, RequestDelegate next)
    {
        var method = context.Request.Method;
        var path = context.Request.Path;
        var responseStream = context.Response.Body;
        var head = HttpMethods.IsHead(method);
        var capture = head || HttpMethods.IsGet(method) && !context.WebSockets.IsWebSocketRequest && !path.StartsWithSegments("/rails/active_storage") && path != "/account/logo" && !(path.Value?.EndsWith("/avatar", StringComparison.Ordinal) ?? false);
        using var responseBuffer = capture ? new PooledResponseBuffer() : null;
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? path.Value ?? "";
        if (Regex.IsMatch(rawTarget, @"%(?![0-9a-fA-F]{2})", RegexOptions.CultureInvariant))
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Invalid HTTP format, parsing fails.\n");
            return;
        }
        try
        {
            if (HttpMethods.IsHead(method))
            {
                context.Request.Method = "GET";
            }
            if (responseBuffer is not null)
            {
                context.Response.Body = responseBuffer;
                context.Items[HtmlSegmentsResult.CaptureKey] = true;
            }
            if (head) context.Response.OnStarting(() => { context.Response.ContentLength = 0; return Task.CompletedTask; });
            if (!path.StartsWithSegments("/assets") && (!path.StartsWithSegments("/rails/active_storage") || path == "/rails/active_storage/direct_uploads.json"))
            {
                var extension = Path.GetExtension(path.Value ?? "");
                if (extension is ".html" or ".json" or ".turbo_stream" or ".js" or ".xml" || path.StartsWithSegments("/qr_code") && extension.Length > 1)
                {
                    context.Items["RailsFormat"] = extension[1..];
                    context.Request.Path = (path.Value ?? "")[..^extension.Length];
                }
            }
            if (HttpMethods.IsPost(method) && context.Request.HasFormContentType)
            {
                // RawRequestBody bot controllers must still be able to rewind after _method parsing.
                context.Request.EnableBuffering();
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                context.Request.Body.Position = 0;
                var overrideMethod = form["_method"].ToString().ToUpperInvariant();
                if (overrideMethod is "PUT" or "PATCH" or "DELETE") context.Request.Method = overrideMethod;
            }
            await next(context);
            var segments = context.Items.TryGetValue(HtmlSegmentsResult.SegmentsKey, out var deferred)
                ? (IReadOnlyList<HtmlSegment>)deferred! : null;
            if (responseBuffer is not null && context.Response.StatusCode is 200 or 201 && !context.Response.HasStarted)
            {
                if (context.Response.Headers.ETag.Count == 0 && context.Response.Headers.LastModified.Count == 0)
                    context.Response.Headers.ETag = "W/\"" + Convert.ToHexStringLower(segments is null ? responseBuffer.Sha256() : SegmentDigest(segments))[..32] + "\"";
                if (context.Response.Headers.CacheControl.Count == 0) context.Response.Headers.CacheControl = "max-age=0, private, must-revalidate";
                if (!context.Response.Headers.Vary.ToString().Split(',').Any(value => value.Trim() == "Accept")) context.Response.Headers.Append("Vary", "Accept");
                var etag = context.Response.Headers.ETag.ToString();
                // Pinned Rack 3.2.6 ConditionalGet compares the complete header
                // literally; wildcard, lists and weak/strong normalization differ.
                if (etag.Length > 0 && context.Request.Headers.IfNoneMatch.ToString() == etag)
                {
                    context.Response.StatusCode = 304;
                    context.Response.ContentLength = null;
                    context.Response.ContentType = null;
                    responseBuffer.SetLength(0);
                    segments = null;
                }
            }
            context.Response.Body = responseStream;
            if (!head && responseBuffer is not null)
            {
                if (segments is not null)
                {
                    var compression = context.RequestServices?.GetService<IResponseCompressionProvider>();
                    var encoder = context.RequestServices?.GetService<FragmentGzipEncoder>();
                    if (encoder is not null && compression is not null &&
                        context.Response.StatusCode == 200 &&
                        compression.ShouldCompressResponse(context) &&
                        compression.GetCompressionProvider(context)?.EncodingName == "gzip" &&
                        encoder.TryEncode(segments, out var encoded))
                    {
                        // An existing Content-Encoding makes the outer ASP.NET
                        // compression middleware pass these gzip bytes through.
                        context.Response.Headers.ContentEncoding = "gzip";
                        context.Response.ContentLength = encoded.Length;
                        if (!context.Response.Headers.Vary.ToString().Split(',').Any(value => value.Trim().Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)))
                            context.Response.Headers.Append("Vary", "Accept-Encoding");
                        await responseStream.WriteAsync(encoded, context.RequestAborted);
                    }
                    else await WriteSegments(responseStream, segments, context.RequestAborted);
                }
                else
                {
                    responseBuffer.Position = 0;
                    await responseBuffer.CopyToAsync(responseStream, context.RequestAborted);
                }
            }
        }
        finally
        {
            context.Request.Method = method;
            context.Request.Path = path;
            context.Response.Body = responseStream;
            context.Items.Remove(HtmlSegmentsResult.CaptureKey);
            context.Items.Remove(HtmlSegmentsResult.SegmentsKey);
        }
    }

    private static byte[] SegmentDigest(IReadOnlyList<HtmlSegment> segments)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Small form/token slices otherwise cause hundreds of native hash calls.
        // Keep the exact whole-body digest while batching their input.
        const int capacity = 32 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            var used = 0;
            foreach (var segment in segments)
            {
                var remaining = segment.Bytes.Span;
                if (used == 0 && remaining.Length >= capacity)
                {
                    hash.AppendData(remaining);
                    continue;
                }
                while (!remaining.IsEmpty)
                {
                    var count = Math.Min(capacity - used, remaining.Length);
                    remaining[..count].CopyTo(buffer.AsSpan(used));
                    remaining = remaining[count..];
                    used += count;
                    if (used == capacity)
                    {
                        hash.AppendData(buffer.AsSpan(0, used));
                        used = 0;
                    }
                }
            }
            if (used > 0) hash.AppendData(buffer.AsSpan(0, used));
            return hash.GetHashAndReset();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static async Task WriteSegments(Stream destination, IReadOnlyList<HtmlSegment> segments, CancellationToken cancellationToken)
    {
        // Coalesce small form/token pieces before compression without allocating
        // a whole page or making a compressor call for every hidden field.
        const int capacity = 32 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            var used = 0;
            foreach (var segment in segments)
            {
                var remaining = segment.Bytes;
                while (!remaining.IsEmpty)
                {
                    var count = Math.Min(capacity - used, remaining.Length);
                    remaining.Span[..count].CopyTo(buffer.AsSpan(used, count));
                    remaining = remaining[count..];
                    used += count;
                    if (used == capacity)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, used), cancellationToken);
                        used = 0;
                    }
                }
            }
            if (used != 0) await destination.WriteAsync(buffer.AsMemory(0, used), cancellationToken);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public static async Task Defaults(HttpContext context, RequestDelegate next)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "SAMEORIGIN";
            headers["X-Permitted-Cross-Domain-Policies"] = "none";
            headers["X-XSS-Protection"] = "0";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            if (context.Response.StatusCode is not (204 or 304) && context.Response.ContentType is null)
                context.Response.ContentType = "text/html; charset=utf-8";
            return Task.CompletedTask;
        });
        await next(context);
        if (context.Response.StatusCode == 406 && context.Response.ContentType is null && !context.Response.HasStarted)
            await ErrorPage(context, 406);
        if ((context.GetEndpoint() is null && context.Response.StatusCode == 404 || context.Response.StatusCode == 405) && !context.Response.HasStarted)
        {
            context.Response.StatusCode = 404;
            // Rails never binds a route format for an unmatched path.
            context.Items.Remove("RailsFormat");
            await ErrorPage(context, 404);
        }
    }

    public static async Task BrowserAndVersion(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path == "/up" || context.GetEndpoint()?.Metadata.GetMetadata<RailsEngineController>() is not null) { await next(context); return; }
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        context.Response.Headers["X-Version"] = configuration["APP_VERSION"] is { Length: > 0 } version ? version : configuration["GIT_REVISION"] is { Length: > 0 } revision ? revision : "0";
        if (configuration["GIT_REVISION"] is { Length: > 0 } gitRevision) context.Response.Headers["X-Rev"] = gitRevision;
        if (IsUnsupported(context.Request.Headers.UserAgent.ToString()))
        {
            var renderer = context.RequestServices.GetRequiredService<IPageRenderer>();
            var choices = new[] { ("safari", "17.2"), ("chrome", "120"), ("firefox", "121"), ("opera", "104") };
            var browsers = string.Join("", choices.Select(b => $"<div class=\"browser flex flex-column\"><img src=\"{WebUtility.HtmlEncode(renderer.Asset("browsers/" + b.Item1 + ".svg"))}\" aria-hidden=\"true\" class=\"center\"><div class=\"flex flex-column align-center margin-block-start-half\"><strong>{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(b.Item1)}</strong><span> {b.Item2}+</span></div></div>"));
            var body = "<div class=\"panel center\"><header><h1 class=\"txt-x-large txt-tight-lines txt-align-center margin-none-block-start margin-block-end\">Upgrade to a supported web browser</h1><div class=\"flex align-start gap\">" + renderer.Translation("incompatible_browser_messsage") + "<p class=\"margin-none-block-start\">Campfire requires a modern web browser. Please use one of the browsers listed below and make sure auto-updates are enabled.</p></div></header><div class=\"browser-list flex align-center flex-wrap gap justify-center margin-block\">" + browsers + "</div></div>";
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(renderer.Layout(context.User() ?? new UserRecord(), body, "Unsupported browser"));
            return;
        }
        await next(context);
    }

    public static bool IsUnsupported(string userAgent)
    {
        if (userAgent.Contains("MSIE", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("Trident/", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var (pattern, minimum) in new[] { (@"OPR/(\d+)", 104d), (@"(?:Chrome|Chromium)/(\d+)", 120d), (@"Firefox/(\d+)", 121d), (@"Version/(\d+(?:\.\d+)?).*Safari/", 17.2d) })
        {
            var match = Regex.Match(userAgent, pattern, RegexOptions.CultureInvariant);
            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var version)) return version < minimum;
        }
        return false;
    }

    public static async Task ErrorPage(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        var format = context.Items["RailsFormat"] as string;
        if (format is null)
        {
            var accepted = context.Request.Headers.Accept.ToString();
            var first = accepted.Split(',')[0].Split(';')[0].Trim();
            if (first == "application/json" || accepted.Length == 0 && context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true) format = "json";
            else if (first is "application/xml" or "text/xml") format = "xml";
        }
        var reason = Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status);
        if (format is "json" or "xml")
        {
            context.Response.ContentType = format == "json" ? "application/json; charset=utf-8" : "application/xml; charset=utf-8";
            if (HttpMethods.IsHead(context.Request.Method)) { context.Response.ContentLength = 0; return; }
            await context.Response.WriteAsync(format == "json"
                ? $"{{\"status\":{status},\"error\":\"{reason}\"}}"
                : $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<hash>\n  <status type=\"integer\">{status}</status>\n  <error>{WebUtility.HtmlEncode(reason)}</error>\n</hash>\n");
            return;
        }
        var environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var root = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
        var file = Path.Combine(root, status + ".html");
        context.Response.ContentType = "text/html; charset=utf-8";
        if (HttpMethods.IsHead(context.Request.Method)) { context.Response.ContentLength = 0; return; }
        await context.Response.WriteAsync(File.Exists(file) ? await File.ReadAllTextAsync(file, context.RequestAborted) : "");
    }
}
