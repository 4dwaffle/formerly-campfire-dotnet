using Dapper;
using System.IO.Compression;
using System.Text.Json;
using Campfire.Features.Chat;
using Campfire.Features.Identity;
using Campfire.Features.Integrations;
using Campfire.Features.Realtime;
using Campfire.Features.Storage;
using Campfire.Features.WebSupport;
using Campfire.Contracts;
using Campfire.Features.Operations;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;

var prepareBackup = args.Contains("--prepare-backup", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(argument => argument != "--prepare-backup").ToArray());
if (prepareBackup)
{
    BackupOperations.PrepareBackup(builder.Configuration);
    return;
}
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 100 * 1024 * 1024);
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["text/vnd.turbo-stream.html", "image/svg+xml"]);
});
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
// Reuse validated immutable compression plans; false selects ordinary gzip.
if (!string.Equals(builder.Configuration["CAMPFIRE_FRAGMENT_GZIP"], "false", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<FragmentGzipEncoder>();
builder.Services.AddIdentityFeature(builder.Configuration);
builder.Services.AddChatFeature(builder.Configuration);
builder.Services.AddRealtimeFeature(builder.Configuration);
builder.Services.AddStorageFeature(builder.Configuration);
builder.Services.AddIntegrationsFeature(builder.Configuration);

var app = builder.Build();
var database = app.Services.GetRequiredService<IDataStore>();
_ = app.Services.GetRequiredService<IRailsCrypto>();
database.Write((queryConnection1,queryTransaction1) => queryConnection1.Execute("UPDATE memberships SET connected_at=NULL, connections=0, updated_at=@now WHERE connected_at>=@threshold",    new { now = RequestUser.Timestamp(), threshold = DateTime.UtcNow.AddSeconds(-60).ToString("yyyy-MM-dd HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture) },transaction: queryTransaction1));
app.Use(RailsHttpCompatibility.Defaults);
app.UseExceptionHandler(error => error.Run(context => RailsHttpCompatibility.ErrorPage(context, 500)));
app.Use(RailsRemoteIp.Capture);
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
if (app.Environment.IsProduction() && string.IsNullOrWhiteSpace(app.Configuration["DISABLE_SSL"]))
{
    // Rails production uses both assume_ssl and force_ssl behind its TLS terminator.
    app.Use(async (context, next) =>
    {
        context.Request.Scheme = "https";
        context.Response.Headers.StrictTransportSecurity = "max-age=63072000; includeSubDomains";
        await next(context);
    });
}
app.UseResponseCompression();
var assetContentTypes = new FileExtensionContentTypeProvider();
assetContentTypes.Mappings[".br"] = "text/plain";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = assetContentTypes,
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "public, max-age=2592000";
    }
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
app.Use(RailsHttpCompatibility.NormalizeRequest);
app.UseRouting();
app.Use(RailsHttpCompatibility.BrowserAndVersion);
app.Use(async (context, next) =>
{
    if (context.GetEndpoint() is null || context.GetEndpoint()?.Metadata.GetMetadata<RailsEngineController>() is { RequireCsrf: false }) { await next(context); return; }
    if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
    {
        var requestDatabase = context.RequestServices.GetRequiredService<IDataStore>();
        var address = RailsRemoteIp.Address(context);
        if (context.GetEndpoint()?.Metadata.GetMetadata<RailsEngineController>() is null && address is not null && requestDatabase.Read(queryConnection2 => queryConnection2.ExecuteScalar<long>("SELECT count(*) FROM bans WHERE ip_address=@address",new { address })) > 0)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        // Active Storage disk PUTs authorize the request with a purpose-bound signed token.
        if (!(context.Request.Method == "PUT" && context.Request.Path.StartsWithSegments("/rails/active_storage/disk")))
        {
            string? token = null;
            if (context.Request.HasFormContentType)
                token = (await context.Request.ReadFormAsync(context.RequestAborted))["authenticity_token"].FirstOrDefault();
            else if (context.Request.ContentType?.Split(';')[0].Trim() == "application/json")
            {
                context.Request.EnableBuffering();
                try
                {
                    using var parameters = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                    if (parameters.RootElement.ValueKind == JsonValueKind.Object && parameters.RootElement.TryGetProperty("authenticity_token", out var value) && value.ValueKind == JsonValueKind.String)
                        token = value.GetString();
                }
                catch (JsonException)
                {
                    await RailsHttpCompatibility.ErrorPage(context, 400);
                    return;
                }
                finally { context.Request.Body.Position = 0; }
            }
            if (!context.RequestServices.GetRequiredService<IAuthService>().ValidateCsrf(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                return;
            }
        }
    }
    await next(context);
});
app.MapGet("/up", (HttpContext context) =>
{
    const string tag = "W/\"7e6c9877b2dec7dfadc43e742246d94d\"";
    context.Response.Headers.ETag = tag;
    context.Response.Headers.CacheControl = "max-age=0, private, must-revalidate";
    context.Response.Headers.Vary = "Accept,Accept-Encoding";
    return context.Request.Headers.IfNoneMatch.ToString().Split(',').Any(value => value.Trim() == tag)
        ? Results.StatusCode(304)
        : Results.Text("<!DOCTYPE html><html><body style=\"background-color: green\"></body></html>", "text/html; charset=utf-8");
});
app.MapIdentityFeature();
app.MapChatFeature();
app.MapRealtimeFeature();
app.MapStorageFeature();
app.MapIntegrationsFeature();
app.MapWebSupportFeature();
app.MapRailsFrameworkFeature();
app.Run();

public partial class Program;
