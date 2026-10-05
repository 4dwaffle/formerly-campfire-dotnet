using System.Text.Json.Serialization;

namespace Campfire.Features.Integrations;

internal sealed record VapidClaims(string aud, long exp, string sub);
internal sealed record WebhookUser(long id, string name);
internal sealed record WebhookRoom(long id, string? name, string path);
internal sealed record WebhookBody(string html, string plain);
internal sealed record WebhookMessage(long id, WebhookBody body, string path);
internal sealed record WebhookPayload(WebhookUser user, WebhookRoom room, WebhookMessage message);
internal sealed record NotificationData(string path, long badge);
internal sealed record NotificationOptions(string body, string icon, NotificationData data);
internal sealed record PushPayload(string title, NotificationOptions options);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VapidClaims))]
[JsonSerializable(typeof(WebhookPayload))]
[JsonSerializable(typeof(PushPayload))]
[JsonSerializable(typeof(OpenGraphMetadata))]
internal partial class IntegrationsJson : JsonSerializerContext;
