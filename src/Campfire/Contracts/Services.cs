using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Campfire.Contracts;

public interface IDataStore
{
    string DatabasePath { get; }
    T Read<T>(Func<SqliteConnection, T> operation);
    // JIT compatibility/test helpers. Production queries use Read/Write callbacks
    // with concrete Dapper types so their factories can be generated for Native AOT.
    List<T> Query<T>(string sql, object? parameters = null);
    T? Single<T>(string sql, object? parameters = null);
    T Scalar<T>(string sql, object? parameters = null);
    int Execute(string sql, object? parameters = null);
    T Write<T>(Func<SqliteConnection, SqliteTransaction, T> operation);
    T Write<T>(Func<SqliteConnection, SqliteTransaction, T> operation, Action<SqliteConnection, T> afterCommit)
    {
        var result = Write(operation);
        Read(connection => { afterCommit(connection, result); return 0; });
        return result;
    }
    Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> operation, CancellationToken cancellationToken = default);
    async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> operation, Action<SqliteConnection, T> afterCommit, CancellationToken cancellationToken = default)
    {
        var result = await WriteAsync(operation, cancellationToken);
        Read(connection => { afterCommit(connection, result); return 0; });
        return result;
    }
}

public sealed class UserRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? EmailAddress { get; set; }
    public string? PasswordDigest { get; set; }
    public string? Bio { get; set; }
    public string? BotToken { get; set; }
    public int Role { get; set; }
    public int Status { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public bool IsAdmin => Role == 1;
}

public interface IAuthService
{
    UserRecord? Current(HttpContext context);
    string CsrfToken(HttpContext context);
    bool ValidateCsrf(HttpContext context, string? formToken = null);
    void SignIn(HttpContext context, long userId);
    void SignOut(HttpContext context);
    void CaptureReturnTo(HttpContext context);
    string ConsumeReturnTo(HttpContext context);
}

public interface IRailsCrypto
{
    string Sign(string value, string purpose = "");
    string? Verify(string value, string purpose = "");
    string SignedId(string model, long id, string purpose);
    long? VerifySignedId(string token, string model, string purpose);
    string SignStream(string stream);
    string? VerifyStream(string token);
    string SignStorage(string rawJson, string purpose, DateTimeOffset? expires = null);
    string? VerifyStorage(string token, string purpose);
    string SignedGlobalId(string model, long id, string purpose = "attachable");
    long? VerifySignedGlobalId(string token, string model, string purpose = "attachable");
}

public interface IChatRenderer
{
    string MessageHtml(long messageId, long viewerId);
    string BroadcastMessageHtml(long messageId, long viewerId) => MessageHtml(messageId, viewerId);
}

public interface IPageRenderer
{
    string Translation(string key) => "";
    string InstallInstructions(HttpContext context) => "";
    string Layout(UserRecord user, string body, string title = "Campfire", string nav = "", string footer = "", string sidebar = "", string bodyClass = "");
    string Asset(string logical);
    string Icon(string name, int size = 20);
    string Avatar(UserRecord user);
}

public interface IRealtimeEvents
{
    Task MessageChangedAsync(long roomId, long messageId, string action = "append", string? clientMessageId = null);
    Task TurboStreamAsync(string stream, string action, string target, string? html = null, bool maintainScroll = false);
    Task DisconnectUserAsync(long userId, bool reconnect = false);
}

public interface IPreparedRecordUpload : IAsyncDisposable
{
    Task CompleteAsync(CancellationToken cancellationToken = default);
}

public interface IMediaService
{
    Task<IPreparedRecordUpload> SaveRecordUploadInTransactionAsync(SqliteConnection connection, SqliteTransaction transaction, string recordType, long recordId, string name, IFormFile file, CancellationToken cancellationToken = default);
    Task<IPreparedRecordUpload> SaveRecordUploadInTransactionAsync(SqliteConnection connection, SqliteTransaction transaction, string recordType, long recordId, string name, string attachable, CancellationToken cancellationToken = default);
    Task SaveUploadAsync(long messageId, IFormFile file, CancellationToken cancellationToken = default);
    Task SaveRecordUploadAsync(string recordType, long recordId, string name, IFormFile file, CancellationToken cancellationToken = default);
    Task PurgeBlobsAsync(IEnumerable<long> blobIds);
    Task ProcessMessageAttachmentAsync(long messageId, CancellationToken cancellationToken = default);
    string AttachmentHtml(long messageId);
}

public interface IRedisTransport
{
    bool Enabled { get; }
    Task<string?> StringAsync(string[] command, CancellationToken cancellationToken = default);
    Task<long> IntegerAsync(string[] command, CancellationToken cancellationToken = default);
    async Task<long[]> IntegersAsync(IReadOnlyList<string[]> commands, CancellationToken cancellationToken = default)
    {
        var replies = new long[commands.Count];
        for (var index = 0; index < commands.Count; index++)
            replies[index] = await IntegerAsync(commands[index], cancellationToken);
        return replies;
    }
    Task<string?[]> ArrayAsync(string[] command, CancellationToken cancellationToken = default);
}

public interface IBackgroundJobs
{
    void Register(string jobClass, Func<JsonElement[], CancellationToken, Task> handler);
    Task EnqueueAsync(string jobClass, JsonNode?[] arguments, CancellationToken cancellationToken = default);
}

public interface IIntegrationEvents
{
    Task MessageCreatedAsync(long messageId);
}

public static class RequestUser
{
    public static UserRecord? User(this HttpContext context) =>
        context.RequestServices.GetRequiredService<IAuthService>().Current(context);
    public static string Timestamp() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture);
}
