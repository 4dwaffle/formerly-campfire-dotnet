using Campfire.Features.Persistence;
using Campfire.Features.WebSupport;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Campfire.Contracts;
using Dapper;

namespace Campfire.Features.Storage;

public static class StorageFeature
{
    public static IServiceCollection AddStorageFeature(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<MediaService>();
        services.AddSingleton<IMediaService>(sp => sp.GetRequiredService<MediaService>());
        return services;
    }
    public static WebApplication MapStorageFeature(this WebApplication app)
    {
        app.MapPost("/rails/active_storage/direct_uploads", (HttpContext context, MediaService media) => media.DirectUpload(context)).WithMetadata(new RailsEngineController(RequireCsrf:true));
        app.MapPut("/rails/active_storage/disk/{token}", (HttpContext context, string token, MediaService media) => media.DiskUpload(context, token)).WithMetadata(new RailsEngineController());
        app.MapGet("/rails/active_storage/disk/{token}/{**filename}", (HttpContext context, string token, MediaService media) => media.DiskDownload(context, token)).WithMetadata(new RailsEngineController());
        foreach (var mode in new[] { "redirect", "proxy" })
        {
            app.MapGet($"/rails/active_storage/blobs/{mode}/{{signedId}}/{{**filename}}", (HttpContext context, string signedId, MediaService media) => media.BlobDownload(context, signedId)).WithMetadata(new RailsEngineController());
            app.MapGet($"/rails/active_storage/representations/{mode}/{{signedId}}/{{variation}}/{{**filename}}", (HttpContext context, string signedId, string variation, MediaService media) => media.Representation(context, signedId, variation)).WithMetadata(new RailsEngineController());
        }
        app.MapGet("/rails/active_storage/blobs/{signedId}/{**filename}", (HttpContext context, string signedId, MediaService media) => media.BlobDownload(context, signedId)).WithMetadata(new RailsEngineController());
        app.MapGet("/rails/active_storage/representations/{signedId}/{variation}/{**filename}", (HttpContext context, string signedId, string variation, MediaService media) => media.Representation(context, signedId, variation)).WithMetadata(new RailsEngineController());
        app.MapGet("/users/{token}/avatar", (HttpContext context, string token, MediaService media) => media.Avatar(context, token));
        app.MapDelete("/users/{token}/avatar", (HttpContext context, MediaService media) => media.DeleteAvatar(context));
        app.MapGet("/account/logo", (HttpContext context, MediaService media) => media.Logo(context));
        app.MapDelete("/account/logo", (HttpContext context, MediaService media) => media.DeleteLogo(context));
        return app;
    }
}

public sealed class BlobRecord
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Filename { get; set; } = "";
    public string? ContentType { get; set; }
    public string? Metadata { get; set; } = "{}";
    public string ServiceName { get; set; } = "local";
    public long ByteSize { get; set; }
    public string? Checksum { get; set; }
    public string CreatedAt { get; set; } = "";
}

public sealed partial class MediaService(IDataStore db, IRailsCrypto crypto, IConfiguration configuration, IWebHostEnvironment environment, ILogger<MediaService> logger, IServiceProvider services) : IMediaService
{
    private const string Columns = "b.id,b.key,b.filename,b.content_type ContentType,b.metadata,b.service_name ServiceName,b.byte_size ByteSize,b.checksum,b.created_at CreatedAt";
    private readonly string files = Path.GetFullPath(configuration["CAMPFIRE_FILES_PATH"] ?? Path.Combine(configuration["CAMPFIRE_STORAGE"] ?? "storage", "files"));
    private string? AssetPath(string logical)
    {
        var root = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
        var manifest = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifest)) return null;
        using var parsed = JsonDocument.Parse(File.ReadAllText(manifest));
        return parsed.RootElement.TryGetProperty(logical, out var asset) ? Path.Combine(root, "assets", asset.GetProperty("digested_path").GetString()!.Replace('/', Path.DirectorySeparatorChar)) : null;
    }
    public static string Key() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public string DiskPath(string key)
    {
        if (key.Length < 4 || key.Contains('/') || key.Contains('\\') || key.Contains('\0') || key is "." or "..") throw new ArgumentException("Invalid storage key");
        return Path.Combine(files, key[..2], key[2..4], key);
    }
    public BlobRecord? Attached(string type, long id, string name) => db.Read(queryConnection1 => queryConnection1.QuerySingleOrDefault<BlobRecord>($"SELECT {Columns} FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id=b.id WHERE a.record_type=@type AND a.record_id=@id AND a.name=@name ORDER BY a.id LIMIT 1",new { type, id, name }));
    public BlobRecord? FindSigned(string signedId)
    {
        var raw = crypto.VerifyStorage(signedId, "blob_id");
        return long.TryParse(raw, out var id) ? db.Read(queryConnection2 => queryConnection2.QuerySingleOrDefault<BlobRecord>($"SELECT {Columns} FROM active_storage_blobs b WHERE b.id=@id",new { id })) : null;
    }
    public string BlobUrl(BlobRecord blob) => $"/rails/active_storage/blobs/redirect/{crypto.SignStorage(blob.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "blob_id")}/{Uri.EscapeDataString(blob.Filename)}";
    public Task SaveUploadAsync(long messageId, IFormFile file, CancellationToken cancellationToken = default) => SaveRecordUploadAsync("Message", messageId, "attachment", file, cancellationToken);
    public async Task SaveRecordUploadAsync(string recordType, long recordId, string name, IFormFile file, CancellationToken cancellationToken = default)
    {
        if (recordType is not ("Message" or "User" or "Account" or "ChatUpload") || name is not ("attachment" or "avatar" or "logo")) throw new ArgumentException("Invalid attachment owner");
        var blob = new BlobRecord { Key = Key(), Filename = Path.GetFileName(file.FileName), ContentType = file.ContentType, ByteSize = file.Length, CreatedAt = RequestUser.Timestamp() };
        var path = DiskPath(blob.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var committed = false;
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true))
            {
                await file.CopyToAsync(output, cancellationToken);
                if (output.Length != file.Length) throw new InvalidDataException("Upload length mismatch");
                output.Position = 0;
                blob.Checksum = Convert.ToBase64String(await MD5.HashDataAsync(output, cancellationToken));
            }
            blob.ContentType = await Identify(path, blob.Filename, blob.ContentType, cancellationToken);
            var previous = db.Read(queryConnection3 => queryConnection3.Query<long>("SELECT blob_id FROM active_storage_attachments WHERE record_type=@recordType AND record_id=@recordId AND name=@name",new { recordType, recordId, name }).Materialize());
            db.Write((connection, transaction) =>
            {
                blob.Id = connection.ExecuteScalar<long>("INSERT INTO active_storage_blobs(key,filename,content_type,metadata,service_name,byte_size,checksum,created_at) VALUES (@Key,@Filename,@ContentType,@Metadata,@ServiceName,@ByteSize,@Checksum,@CreatedAt); SELECT last_insert_rowid()", blob, transaction);
                connection.Execute("DELETE FROM active_storage_attachments WHERE record_type=@recordType AND record_id=@recordId AND name=@name", new { recordType, recordId, name }, transaction);
                connection.Execute("INSERT INTO active_storage_attachments(blob_id,record_type,record_id,name,created_at) VALUES (@blobId,@recordType,@recordId,@name,@now)", new { blobId = blob.Id, recordType, recordId, name, now = blob.CreatedAt }, transaction);
                return 0;
            });
            committed = true;
            await AnalyzeAsync(blob, cancellationToken);
            if (recordType == "Message") await ProcessAttachmentAsync(blob, cancellationToken);
            foreach (var old in previous) PurgeBlob(old);
        }
        catch { if (!committed) File.Delete(path); throw; }
    }
    public string AttachmentHtml(long messageId)
    {
        var blob = Attached("Message", messageId, "attachment");
        if (blob is null) return "";
        var url = WebUtility.HtmlEncode(BlobUrl(blob));
        var name = WebUtility.HtmlEncode(blob.Filename);
        var download = url + "?disposition=attachment";
        var width = 0d; var height = 0d;
        try { using var metadata = JsonDocument.Parse(blob.Metadata??"{}"); if (metadata.RootElement.TryGetProperty("width", out var w) && metadata.RootElement.TryGetProperty("height", out var h)) { width = w.GetDouble(); height = h.GetDouble(); } } catch (JsonException) { }
        if (width > 0 && height > 0) { var scale = Math.Min(1, Math.Min(1200d / width, 800d / height)); width *= scale; height *= scale; }
        var constraints = width > 0 && height > 0 ? $"class=\"max-inline-size center flex overflow-clip\" style=\"width: {(width / 2).ToString(System.Globalization.CultureInfo.InvariantCulture)}px; aspect-ratio: {(width / height).ToString(System.Globalization.CultureInfo.InvariantCulture)};\"" : "class=\"max-inline-size center overflow-clip\"";
        if (VariableImage(blob)||blob.ContentType=="application/pdf"&&PdfPreviewable)
        {
            var preview = WebUtility.HtmlEncode(RepresentationUrl(blob, DefaultFormat(blob), 1200, 800));
            return $"<div {constraints}><a href=\"{url}\" class=\"flex\" data-lightbox-target=\"image\" data-action=\"lightbox#open\" data-lightbox-url-value=\"{download}\"><img src=\"{preview}\" alt=\"{name}\"{(width > 0 ? $" width=\"{(int)width}\" height=\"{(int)height}\"" : "")} class=\"message__attachment\" loading=\"lazy\"></a></div>";
        }
        if (blob.ContentType?.StartsWith("video/", StringComparison.Ordinal) == true)
            return $"<div {constraints}><video src=\"{url}\" poster=\"{WebUtility.HtmlEncode(RepresentationUrl(blob, "webp", 1200, 800))}\" controls preload=\"none\" width=\"100%\" height=\"100%\" class=\"message__attachment\"></video></div>";
        string Icon(string logical, int size) { var path = AssetPath(logical); return $"<img src=\"/assets/{(path is null ? logical : Path.GetFileName(path))}\" aria-hidden=\"true\" width=\"{size}\" height=\"{size}\">"; }
        return $"<div class=\"flex-inline align-center gap-half\">{Icon("common-file-text.svg", 22)}<span>{name}</span><a href=\"{download}\" class=\"btn message__action-btn hide-in-ios-pwa\" style=\"--width: auto;\">{Icon("download.svg", 20)}<span class=\"for-screen-reader\">Download {name}</span></a><button class=\"btn message__action-btn\" style=\"--width: auto;\" data-controller=\"web-share\" data-action=\"web-share#share\" data-web-share-files-value=\"{download}\">{Icon("share.svg", 20)}<span class=\"for-screen-reader\">Share {name}</span></button></div>";
    }
    private string RepresentationUrl(BlobRecord blob, string format, int width, int height)
    {
        var token = crypto.SignStorage(JsonSerializer.Serialize(new StorageVariation(format, [width, height]), StorageJson.Default.StorageVariation), "variation");
        return $"/rails/active_storage/representations/redirect/{crypto.SignStorage(blob.Id.ToString(), "blob_id")}/{token}/{Uri.EscapeDataString(blob.Filename)}";
    }
    private static string DefaultFormat(BlobRecord blob)
    {
        var extension = Path.GetExtension(blob.Filename).TrimStart('.').ToLowerInvariant();
        if (blob.ContentType == "image/jpeg" && extension is "jpg" or "jpeg") return extension;
        return blob.ContentType switch { "image/jpeg" => "jpg", "image/gif" => "gif", _ => "png" };
    }
    private static bool Authorized(HttpContext context) => context.User() is not null;
    private static async Task<bool> Mutation(HttpContext context)
    {
        if (!Authorized(context)) return false;
        var auth = context.RequestServices.GetRequiredService<IAuthService>();
        var token = context.Request.HasFormContentType ? (await context.Request.ReadFormAsync())["authenticity_token"].ToString() : null;
        return auth.ValidateCsrf(context, token);
    }
    public async Task<IResult> DirectUpload(HttpContext context)
    {
        var auth=context.RequestServices.GetRequiredService<IAuthService>();
        var formToken=context.Request.HasFormContentType?(await context.Request.ReadFormAsync())["authenticity_token"].ToString():null;
        if(!auth.ValidateCsrf(context,formToken))return Results.StatusCode(422);
        try
        {
            using var doc = context.Request.HasFormContentType ? FormUpload(await context.Request.ReadFormAsync()) : await JsonDocument.ParseAsync(context.Request.Body);
            var value = doc.RootElement.GetProperty("blob");
            var size = value.GetProperty("byte_size").GetInt64();
            var checksum = value.GetProperty("checksum").GetString() ?? "";
            if(size<0)return Results.UnprocessableEntity();
            var blob = new BlobRecord { Key = Key(), Filename = Path.GetFileName(value.GetProperty("filename").GetString() ?? "file"), ContentType = value.TryGetProperty("content_type", out var mime) ? mime.GetString() : "application/octet-stream", ByteSize = size, Checksum = checksum, CreatedAt = RequestUser.Timestamp() };
            var hasMetadata=value.TryGetProperty("metadata",out var metadata)&&metadata.ValueKind==JsonValueKind.Object;
            if(hasMetadata)blob.Metadata=metadata.GetRawText();
            blob.Id = db.Write((connection, transaction) => connection.ExecuteScalar<long>("INSERT INTO active_storage_blobs(key,filename,content_type,metadata,service_name,byte_size,checksum,created_at) VALUES (@Key,@Filename,@ContentType,@Metadata,@ServiceName,@ByteSize,@Checksum,@CreatedAt); SELECT last_insert_rowid()", new{blob.Key,blob.Filename,blob.ContentType,Metadata=hasMetadata?blob.Metadata:null,blob.ServiceName,blob.ByteSize,blob.Checksum,blob.CreatedAt}, transaction));
            var token = crypto.SignStorage(JsonSerializer.Serialize(new DiskUploadToken(blob.Key, blob.ContentType, size, checksum, "local"), StorageJson.Default.DiskUploadToken), "blob_token", DateTimeOffset.UtcNow.AddMinutes(5));
            // Active Record's JSON store reads SQL NULL as an empty hash.
            using var emptyMetadata = JsonDocument.Parse("{}");
            var response = new DirectUploadResponse(blob.Id, blob.Key, blob.Filename, blob.ContentType, hasMetadata?metadata.Clone():emptyMetadata.RootElement.Clone(), blob.ServiceName, size, checksum, blob.CreatedAt, crypto.SignStorage(blob.Id.ToString(), "blob_id"), new UploadTarget($"{context.Request.Scheme}://{context.Request.Host}/rails/active_storage/disk/{token}", new Dictionary<string, string> { ["Content-Type"] = blob.ContentType ?? "application/octet-stream", ["Content-MD5"] = checksum }));
            return Results.Json(response, StorageJson.Default.DirectUploadResponse);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or InvalidOperationException) { return Results.UnprocessableEntity(); }
    }
    private static JsonDocument FormUpload(IFormCollection form)
    {
        var value=new System.Text.Json.Nodes.JsonObject();
        foreach(var key in new[]{"filename","content_type","checksum"}) value[key]=form["blob["+key+"]"].ToString();
        value["byte_size"]=long.TryParse(form["blob[byte_size]"],out var size)?size:0;
        var metadata=new System.Text.Json.Nodes.JsonObject();
        foreach(var entry in form) if(entry.Key.StartsWith("blob[metadata][",StringComparison.Ordinal)&&entry.Key.EndsWith(']')) metadata[entry.Key[15..^1]]=entry.Value.ToString();
        if(metadata.Count>0)value["metadata"]=metadata;
        return JsonDocument.Parse(new System.Text.Json.Nodes.JsonObject{["blob"]=value}.ToJsonString());
    }
    public async Task<IResult> DiskUpload(HttpContext context, string token)
    {
        var raw = crypto.VerifyStorage(token, "blob_token");
        if (raw is null) return Results.NotFound();
        string? temporary = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var data = doc.RootElement;
            if (data.TryGetProperty("expires_at", out var expiry) && expiry.GetInt64() < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return Results.NotFound();
            var size = data.GetProperty("content_length").GetInt64();
            if (size < 0 || (context.Request.ContentLength is long length && length != size)) return Results.UnprocessableEntity();
            var expectedType = data.TryGetProperty("content_type", out var expectedMime) ? expectedMime.GetString() : null;
            if (expectedType is not null && context.Request.ContentType?.Split(';')[0] != expectedType) return Results.UnprocessableEntity();
            var path = DiskPath(data.GetProperty("key").GetString()!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Key() + ".upload";
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; long count = 0; int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    count += read; if (count > size) return Results.UnprocessableEntity();
                    await output.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                }
                if (count != size) return Results.UnprocessableEntity();
                output.Position = 0;
                var actual = await MD5.HashDataAsync(output, context.RequestAborted);
                if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(data.GetProperty("checksum").GetString()!))) return Results.UnprocessableEntity();
            }
            File.Move(temporary, path, true);
            return Results.NoContent();
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or KeyNotFoundException) { return Results.NotFound(); }
        finally { if (temporary is not null) File.Delete(temporary); }
    }
    public IResult DiskDownload(HttpContext context, string token)
    {
        var raw = crypto.VerifyStorage(token, "blob_key");
        if (raw is null) return Results.NotFound();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var data = doc.RootElement;
            if (data.TryGetProperty("expires_at", out var expiry) && expiry.GetInt64() < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return Results.NotFound();
            var path = DiskPath(data.GetProperty("key").GetString()!);
            if (!File.Exists(path)) return Results.NotFound();
            context.Response.Headers.CacheControl = "max-age=3600, public";
            var mime = data.TryGetProperty("content_type", out var content) ? content.GetString() : null;
            var disposition = data.TryGetProperty("disposition", out var disp) ? disp.GetString() : null;
            var unsafeType = BinaryMime(mime);
            if (unsafeType) context.Response.Headers.ContentDisposition = "attachment";
            else if (disposition is not null && !disposition.Contains('\r') && !disposition.Contains('\n')) context.Response.Headers.ContentDisposition = disposition;
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(path, unsafeType ? "application/octet-stream" : SafeMime(mime), enableRangeProcessing: true);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or KeyNotFoundException) { return Results.NotFound(); }
    }
    private static string SafeMime(string? mime) => mime is null || mime.Contains('\r') || mime.Contains('\n') ? "application/octet-stream" : mime;
    private static bool BinaryMime(string? mime) => mime is "text/html" or "image/svg+xml" or "application/postscript" or "application/x-shockwave-flash" or "text/xml" or "application/xml" or "application/xhtml+xml" or "application/mathml+xml" or "text/cache-manifest";
    private static bool InlineMime(string? mime) => mime is "image/webp" or "image/avif" or "image/png" or "image/gif" or "image/jpeg" or "image/tiff" or "image/bmp" or "image/vnd.adobe.photoshop" or "image/vnd.microsoft.icon" or "application/pdf";
    public IResult BlobDownload(HttpContext context, string signedId)
    {
        var blob = FindSigned(signedId);
        return blob is null ? Results.NotFound() : DownloadResult(context, blob);
    }
    private IResult DownloadResult(HttpContext context, BlobRecord blob)
    {
        if (context.Request.Path.Value!.Contains("/proxy/", StringComparison.Ordinal)) { context.Response.Headers.CacheControl="max-age=3155695200, public, immutable";return Serve(context, blob); }
        var unsafeType = BinaryMime(blob.ContentType);
        var disposition = unsafeType || !InlineMime(blob.ContentType) || context.Request.Query["disposition"] == "attachment" ? "attachment" : "inline";
        var safeName=blob.Filename.Replace('"','_').Replace('\r','_').Replace('\n','_');
        var value = new DiskReadToken(blob.Key, $"{disposition}; filename=\"{safeName}\"; filename*=UTF-8''{Uri.EscapeDataString(blob.Filename)}", unsafeType?"application/octet-stream":blob.ContentType, blob.ServiceName);
        var token = crypto.SignStorage(JsonSerializer.Serialize(value, StorageJson.Default.DiskReadToken), "blob_key", DateTimeOffset.UtcNow.AddMinutes(5));
        context.Response.Headers.CacheControl = "max-age=300, public";
        return Results.Redirect($"/rails/active_storage/disk/{token}/{Uri.EscapeDataString(blob.Filename)}");
    }
    private IResult Serve(HttpContext context, BlobRecord blob)
    {
        var path = DiskPath(blob.Key);
        if (!File.Exists(path)) return Results.NotFound();
        if (context.Response.Headers.CacheControl.Count == 0) context.Response.Headers.CacheControl = "max-age=3600, public";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var unsafeType = BinaryMime(blob.ContentType);
        var attachment = unsafeType || !InlineMime(blob.ContentType) || context.Request.Query["disposition"] == "attachment";
        if (!attachment) context.Response.Headers.ContentDisposition = $"inline; filename*=UTF-8''{Uri.EscapeDataString(blob.Filename)}";
        var etag = context.Response.Headers.ETag.ToString();
        return Results.File(path, unsafeType ? "application/octet-stream" : SafeMime(blob.ContentType), fileDownloadName: attachment ? blob.Filename : null, enableRangeProcessing: true, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(etag.Length > 0 ? etag : '"' + (blob.Checksum ?? blob.Key) + '"'));
    }
    public async Task<IResult> Representation(HttpContext context, string signedId, string variation)
    {
        var blob = FindSigned(signedId);
        var raw = crypto.VerifyStorage(variation, "variation");
        if (blob is null || raw is null) return Results.NotFound();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var transformed = await Transform(blob, doc.RootElement, context.RequestAborted);
            return transformed is null ? Results.StatusCode(503) : DownloadResult(context, transformed);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { return Results.UnprocessableEntity(); }
    }
    public async Task<IResult> Avatar(HttpContext context, string token)
    {
        if (!Authorized(context)) return Results.Redirect("/session/new");
        var id = crypto.VerifySignedId(token, "User", "avatar");
        if (id is null) return Results.NotFound();
        var user = db.Read(queryConnection5 => queryConnection5.QuerySingleOrDefault<UserRecord>("SELECT id,name,role,updated_at UpdatedAt FROM users WHERE id=@id",new { id }));
        if (user is null) return Results.NotFound();
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.Id + ":" + user.UpdatedAt))).ToLowerInvariant() + "\"";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "max-age=1800, public, stale-while-revalidate=604800";
        if (context.Request.Headers.IfNoneMatch.ToString().Split(',').Any(x => x.Trim() == etag || x.Trim() == "*")) return Results.StatusCode(304);
        var blob = Attached("User", id.Value, "avatar");
        if (blob is not null && VariableImage(blob))
        {
            var output = await Transform(blob, JsonSerializer.SerializeToElement(new StorageVariation("webp", [512, 512]), StorageJson.Default.StorageVariation), context.RequestAborted,true);
            if (output is null) return Results.StatusCode(503);
            context.Response.Headers.CacheControl = "max-age=1800, public, stale-while-revalidate=604800";
            return Serve(context, output);
        }
        context.Response.Headers.CacheControl = "max-age=1800, public, stale-while-revalidate=604800";
        if (user.Role == 2 && AssetPath("default-bot-avatar.svg") is string botAsset && File.Exists(botAsset)) return Results.File(botAsset, "image/svg+xml");
        var initials = string.Concat(System.Text.RegularExpressions.Regex.Matches(user.Name, @"\b\w").Select(x => x.Value));
        string[] colors = ["#AF2E1B", "#CC6324", "#3B4B59", "#BFA07A", "#ED8008", "#ED3F1C", "#BF1B1B", "#736B1E", "#D07B53", "#736356", "#AD1D1D", "#BF7C2A", "#C09C6F", "#698F9C", "#7C956B", "#5D618F", "#3B3633", "#67695E"];
        var background = colors[Crc32(user.Id.ToString()) % colors.Length];
        return Results.Text($"<svg version=\"1.1\" xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 512 512\" class=\"avatar\" aria-hidden=\"true\"><defs><clipPath id=\"porthole\"><circle cx=\"50%\" cy=\"50%\" r=\"50%\" /></clipPath></defs><g><rect width=\"100%\" height=\"100%\" rx=\"50\" fill=\"{background}\" /><text x=\"50%\" y=\"50%\" fill=\"#FFFFFF\" text-anchor=\"middle\" dy=\"0.35em\" {(initials.Length >= 3 ? "textLength=\"85%\" lengthAdjust=\"spacingAndGlyphs\"" : "")} font-family=\"-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica, Arial, sans-serif\" font-size=\"230\" font-weight=\"800\" letter-spacing=\"-5\">{WebUtility.HtmlEncode(initials)}</text></g></svg>", "image/svg+xml");
    }
    private static uint Crc32(string text)
    {
        var value = uint.MaxValue;
        foreach (var item in Encoding.UTF8.GetBytes(text)) { value ^= item; for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320); }
        return ~value;
    }
    // Campfire's vips initializer removes BMP/ICO/PSD from framework defaults.
    private static bool VariableImage(BlobRecord blob) => blob.ContentType is "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/tiff" or "image/avif" or "image/heic" or "image/heif";
    public async Task<IResult> DeleteAvatar(HttpContext context)
    {
        if (!Authorized(context)) return Results.Redirect("/session/new");
        if (!await Mutation(context)) return Results.StatusCode(422);
        PurgeAttachment("User", context.User()!.Id, "avatar");
        return Results.Redirect("/users/me/profile");
    }
    public async Task<IResult> Logo(HttpContext context)
    {
        var account = db.Read(queryConnection6 => queryConnection6.ExecuteScalar<long?>("SELECT id FROM accounts LIMIT 1"));
        var small = context.Request.Query["size"] == "small";
        var updated = db.Read(queryConnection7 => queryConnection7.ExecuteScalar<string?>("SELECT updated_at FROM accounts LIMIT 1")) ?? "";
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((account?.ToString() ?? "none") + ":" + updated))).ToLowerInvariant() + "\"";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "max-age=300, public, stale-while-revalidate=604800";
        if (context.Request.Headers.IfNoneMatch.ToString().Split(',').Any(x => x.Trim() == etag || x.Trim() == "*")) return Results.StatusCode(304);
        var blob = account is null ? null : Attached("Account", account.Value, "logo");
        if (blob is not null && VariableImage(blob))
        {
            var result = await Transform(blob, JsonSerializer.SerializeToElement(new StorageVariation("png", [small ? 192 : 512, small ? 192 : 512]), StorageJson.Default.StorageVariation), context.RequestAborted,true);
            return result is null ? Results.StatusCode(503) : Serve(context, result);
        }
        var stock = AssetPath(small ? "logos/app-icon-192.png" : "logos/app-icon.png");
        context.Response.Headers.CacheControl = "max-age=300, public, stale-while-revalidate=604800";
        return stock is not null && File.Exists(stock) ? Results.File(stock, "image/png") : Results.NotFound();
    }
    public async Task<IResult> DeleteLogo(HttpContext context)
    {
        if (!Authorized(context)) return Results.Redirect("/session/new");
        if (!context.User()!.IsAdmin) return Results.StatusCode(403);
        if (!await Mutation(context)) return Results.StatusCode(422);
        var account = db.Read(queryConnection8 => queryConnection8.ExecuteScalar<long>("SELECT id FROM accounts LIMIT 1"));
        PurgeAttachment("Account", account, "logo");
        return Results.Redirect("/account/edit");
    }
    public void PurgeAttachment(string type, long id, string name)
    {
        var blobs = db.Write((connection, transaction) =>
        {
            var ids = connection.Query<long>("SELECT blob_id FROM active_storage_attachments WHERE record_type=@type AND record_id=@id AND name=@name", new { type, id, name }, transaction).ToArray();
            connection.Execute("DELETE FROM active_storage_attachments WHERE record_type=@type AND record_id=@id AND name=@name", new { type, id, name }, transaction);
            var table = type switch { "User" => "users", "Account" => "accounts", "Message" => "messages", "ChatUpload" => null, _ => throw new ArgumentException("Invalid owner") };
            if (table is not null) connection.Execute($"UPDATE {table} SET updated_at=@now WHERE id=@id", new { now = RequestUser.Timestamp(), id }, transaction);
            return ids;
        });
        PurgeBlobsAsync(blobs).GetAwaiter().GetResult();
    }
    public async Task PurgeBlobsAsync(IEnumerable<long> blobIds)
    {
        var jobs=services.GetService<Campfire.Features.Integrations.RailsJobQueue>();
        foreach (var id in blobIds.Distinct())
            if(jobs?.Enabled==true)await jobs.EnqueueAsync("ActiveStorage::PurgeJob",[Campfire.Features.Integrations.RailsJobQueue.GlobalId("ActiveStorage::Blob",id)]);
            else PurgeBlob(id);
    }
    private void PurgeBlob(long blob)
    {
        // A previous post-commit disk deletion can be retried even after its
        // blob row disappeared. Move bytes aside during the transaction and
        // restore them if the transaction rolls back.
        if(Directory.Exists(files))foreach(var pending in Directory.EnumerateFiles(files,"*.purge-"+blob+"-*",SearchOption.AllDirectories))File.Delete(pending);
        var moved=new List<(string Original,string Pending)>();
        try { db.Write((connection, transaction) =>
        {
            var files = new List<string>();
            void Remove(long id)
            {
                if (connection.ExecuteScalar<long>("SELECT count(*) FROM active_storage_attachments WHERE blob_id=@id", new { id }, transaction) != 0) return;
                var children = connection.Query<long>("SELECT blob_id FROM active_storage_attachments WHERE (record_type='ActiveStorage::VariantRecord' AND record_id IN (SELECT id FROM active_storage_variant_records WHERE blob_id=@id)) OR (record_type='ActiveStorage::Blob' AND record_id=@id AND name='preview_image')", new { id }, transaction).ToArray();
                connection.Execute("DELETE FROM active_storage_attachments WHERE (record_type='ActiveStorage::VariantRecord' AND record_id IN (SELECT id FROM active_storage_variant_records WHERE blob_id=@id)) OR (record_type='ActiveStorage::Blob' AND record_id=@id AND name='preview_image'); DELETE FROM active_storage_variant_records WHERE blob_id=@id", new { id }, transaction);
                foreach (var child in children) Remove(child);
                var key = connection.ExecuteScalar<string?>("SELECT key FROM active_storage_blobs WHERE id=@id", new { id }, transaction);
                if(key is not null&&File.Exists(DiskPath(key))){var original=DiskPath(key);var pending=original+".purge-"+blob+"-"+Key();File.Move(original,pending);moved.Add((original,pending));}
                connection.Execute("DELETE FROM active_storage_blobs WHERE id=@id", new { id }, transaction);
                if (key is not null) files.Add(DiskPath(key));
            }
            Remove(blob);
            return files;
        }); }
        catch {foreach(var (original,pending) in moved.AsEnumerable().Reverse())if(File.Exists(pending))File.Move(pending,original,true);throw;}
        foreach(var (_,pending) in moved)File.Delete(pending);
    }
    public async Task<BlobRecord?> Transform(BlobRecord blob, JsonElement variation, CancellationToken cancellationToken,bool symbolFormat=false)
    {
        if (blob.ContentType?.StartsWith("video/", StringComparison.Ordinal) == true || blob.ContentType=="application/pdf")
        {
            var preview = await PreviewAsync(blob, cancellationToken);
            if (preview is null) return null;
            return await Transform(preview, variation, cancellationToken,symbolFormat);
        }
        if (!VariableImage(blob)) throw new ArgumentException("Blob cannot be transformed");
        var entries = variation.EnumerateObject().ToList();
        if(entries.Any(x=>!System.Text.RegularExpressions.Regex.IsMatch(x.Name,"^[a-z][a-z0-9_]{0,63}$")))throw new ArgumentException("Invalid transform name");
        var format = variation.TryGetProperty("format", out var fmt) ? fmt.GetString() ?? "png" : DefaultFormat(blob);
        if(!System.Text.RegularExpressions.Regex.IsMatch(format,"^[A-Za-z0-9]{1,16}$"))throw new ArgumentException("Invalid image format");
        if (!variation.TryGetProperty("format", out _))
        {
            var normalized = new Dictionary<string, JsonElement> { ["format"] = JsonSerializer.SerializeToElement(format, StorageJson.Default.String) };
            foreach (var entry in variation.EnumerateObject()) normalized[entry.Name] = entry.Value.Clone();
            variation = JsonSerializer.SerializeToElement(normalized, StorageJson.Default.DictionaryStringJsonElement);
        }
        var width = 0; var height = 0;
        if (variation.TryGetProperty("resize_to_limit", out var resize)) { width = resize[0].ValueKind==JsonValueKind.Null?0:resize[0].GetInt32(); height = resize[1].ValueKind==JsonValueKind.Null?0:resize[1].GetInt32(); }
        if (width < 0 || height < 0 || variation.TryGetProperty("resize_to_limit", out _) && width==0&&height==0) throw new ArgumentException("Invalid size");
        var digest = RubyVariation.Digest(variation,symbolFormat);
        await using var variantLock=await VariantLock(blob.Id,digest,cancellationToken);
        try
        {
            var existing = db.Read(queryConnection9 => queryConnection9.QuerySingleOrDefault<BlobRecord>($"SELECT {Columns} FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id=b.id JOIN active_storage_variant_records v ON v.id=a.record_id WHERE a.record_type='ActiveStorage::VariantRecord' AND a.name='image' AND v.blob_id=@id AND v.variation_digest=@digest LIMIT 1",new { id = blob.Id, digest }));
            if (existing is not null && File.Exists(DiskPath(existing.Key))) return existing;
            var source = DiskPath(blob.Key);
            if (!File.Exists(source)) return null;
            if(!await Integrity(blob,cancellationToken))throw new InvalidDataException("Active Storage integrity check failed");
            var outputKey = Key(); var outputPath = DiskPath(outputKey); Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var temporary = outputPath + "." + format;
            try
            {
                if (!await TransformImage(source,temporary,variation,cancellationToken)) return null;
                File.Move(temporary, outputPath, true);
                var output = new BlobRecord { Key = outputKey, Filename = Path.ChangeExtension(blob.Filename, format), ContentType = MarcelMime.ForExtension("variant."+format)??"application/octet-stream", ByteSize = new FileInfo(outputPath).Length, CreatedAt = RequestUser.Timestamp(), Metadata = "{\"identified\":true,\"analyzed\":true}" };
                await using (var stream = File.OpenRead(outputPath)) output.Checksum = Convert.ToBase64String(await MD5.HashDataAsync(stream, cancellationToken));
                db.Write((connection, transaction) =>
                {
                    output.Id = connection.ExecuteScalar<long>("INSERT INTO active_storage_blobs(key,filename,content_type,metadata,service_name,byte_size,checksum,created_at) VALUES (@Key,@Filename,@ContentType,@Metadata,@ServiceName,@ByteSize,@Checksum,@CreatedAt);SELECT last_insert_rowid()", output, transaction);
                    connection.Execute("INSERT INTO active_storage_variant_records(blob_id,variation_digest) VALUES (@id,@digest) ON CONFLICT(blob_id,variation_digest) DO NOTHING", new { id = blob.Id, digest }, transaction);
                    var variant = connection.ExecuteScalar<long>("SELECT id FROM active_storage_variant_records WHERE blob_id=@id AND variation_digest=@digest", new { id = blob.Id, digest }, transaction);
                    connection.Execute("INSERT INTO active_storage_attachments(blob_id,record_type,record_id,name,created_at) VALUES (@id,'ActiveStorage::VariantRecord',@variant,'image',@now)", new { id = output.Id, variant, now = output.CreatedAt }, transaction);
                    return 0;
                });
                return output;
            }
            catch { File.Delete(outputPath);throw; }
            finally { File.Delete(temporary); }
        }
        finally { }
    }
    private async Task<FileStream> VariantLock(long id,string digest,CancellationToken cancellationToken)
    {
        var directory=Path.Combine(files,".variant-locks");Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,id+"-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digest))));
        while(true){cancellationToken.ThrowIfCancellationRequested();try{return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){await Task.Delay(25,cancellationToken);}}
    }
    private async Task<bool> Process(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            if(executable=="vips" && configuration["CAMPFIRE_VIPS_COMMAND"] is string helper) executable=helper;
            var start = new ProcessStartInfo(executable) { RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            start.Environment["VIPS_BLOCK_UNTRUSTED"]="1";
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); return false; }
            await stdout;
            if (process.ExitCode == 0) return true;
            logger.LogWarning("Media processing failed: {Error}", await errors);
            return false;
        }
        catch (System.ComponentModel.Win32Exception) { logger.LogWarning("Media executable {Executable} unavailable", executable); return false; }
    }
}
