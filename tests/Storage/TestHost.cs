using System.Net;
using Campfire.Contracts;
using Campfire.Features.Identity;
using Campfire.Features.Persistence;
using Campfire.Features.Realtime;
using Campfire.Features.Storage;
using Campfire.Features.Integrations;

namespace Campfire.FeatureTests;

public sealed class FeatureHost : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required HttpClient Client { get; init; }
    public required string Root { get; init; }
    public IDataStore Db => App.Services.GetRequiredService<IDataStore>();
    public IRailsCrypto Crypto => App.Services.GetRequiredService<IRailsCrypto>();
    public MediaService Media => App.Services.GetRequiredService<MediaService>();
    public Uri WebSocketUri => new(Client.BaseAddress!.AbsoluteUri.Replace("http://", "ws://") + "cable");
    public static async Task<FeatureHost> Start(bool deliverIntegrations = false,IDictionary<string,string?>? configuration=null)
    {
        var root = Path.Combine(Path.GetTempPath(), "campfire-feature-" + Guid.NewGuid().ToString("N"));
        var repository = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "src", "Campfire", "Campfire.csproj"))) repository = repository.Parent;
        if (repository is null) throw new InvalidOperationException("Repository root missing");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = repository.FullName, WebRootPath = Path.Combine(repository.FullName, "src", "Campfire", "wwwroot") });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CAMPFIRE_STORAGE"] = root, ["SECRET_KEY_BASE"] = new string('a', 128), ["CAMPFIRE_DELIVER_INTEGRATIONS"] = deliverIntegrations ? "true" : "false" });
        if(configuration is not null)builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IDataStore, SqliteDataStore>();
        builder.Services.AddSingleton<IRailsCrypto, RailsCrypto>();
        builder.Services.AddSingleton<IAuthService, TestAuth>();
        builder.Services.AddSingleton<IChatRenderer, TestRenderer>();
        builder.Services.AddRealtimeFeature(builder.Configuration).AddStorageFeature(builder.Configuration).AddIntegrationsFeature(builder.Configuration);
        var app = builder.Build();
        app.MapRealtimeFeature().MapStorageFeature().MapIntegrationsFeature();
        await app.StartAsync();
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
        var host = new FeatureHost { App = app, Client = client, Root = root };
        host.Db.Execute("INSERT INTO accounts(id,name,join_code,created_at,updated_at) VALUES(1,'Test','code',@now,@now); INSERT INTO users(id,name,role,status,created_at,updated_at) VALUES(1,'Alice',1,0,@now,@now),(2,'Bob',0,0,@now,@now),(3,'Bot',2,0,@now,@now); INSERT INTO rooms(id,name,type,creator_id,created_at,updated_at) VALUES(1,'Private','Rooms::Closed',1,@now,@now),(2,'Secret','Rooms::Closed',1,@now,@now); INSERT INTO memberships(user_id,room_id,involvement,created_at,updated_at,unread_at) VALUES(1,1,'everything',@now,@now,@now),(2,2,'everything',@now,@now,@now); INSERT INTO messages(id,creator_id,room_id,client_message_id,created_at,updated_at) VALUES(1,1,1,'client-uuid',@now,@now); INSERT INTO action_text_rich_texts(record_type,record_id,name,body,created_at,updated_at) VALUES('Message',1,'body','Hello',@now,@now)", new { now = RequestUser.Timestamp() });
        return host;
    }
    public static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose(); await App.StopAsync(); await App.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(Root, true);
    }
    private sealed class TestAuth(IDataStore db) : IAuthService
    {
        public UserRecord? Current(HttpContext context)
        {
            var raw = context.Request.Cookies["test_user"];
            return long.TryParse(raw, out var id) ? db.Single<UserRecord>("SELECT * FROM users WHERE id=@id AND status=0", new { id }) : null;
        }
        public string CsrfToken(HttpContext context) => "test-csrf";
        public bool ValidateCsrf(HttpContext context, string? formToken = null) => context.Request.Headers["X-CSRF-Token"] == "test-csrf" || formToken == "test-csrf";
        public void SignIn(HttpContext context, long userId) => throw new NotSupportedException();
        public void SignOut(HttpContext context) => throw new NotSupportedException();
        public void CaptureReturnTo(HttpContext context) => context.Items["return-to"] = context.Request.Path.ToString();
        public string ConsumeReturnTo(HttpContext context) => context.Items["return-to"] as string ?? "/";
    }
    private sealed class TestRenderer : IChatRenderer { public string MessageHtml(long messageId, long viewerId) => $"<div id=\"message_client-uuid\" data-viewer=\"{viewerId}\">Actual rendered message {messageId}</div>"; }
}
