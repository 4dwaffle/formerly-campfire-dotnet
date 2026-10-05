using System.Text.Json.Serialization;

namespace Campfire.Features.Realtime;

internal sealed record CableControl(string type, string? identifier = null, string? reason = null, bool? reconnect = null);
internal sealed record CablePing(string type, long message);
internal sealed record CableMessage<T>(string identifier, T message);
internal sealed record ReadRoom(long room_id);
internal sealed record UnreadRoom(long roomId);
internal sealed record TypingUser(long id, string name);
internal sealed record TypingNotification(string? action, TypingUser user);
internal sealed record RealtimeEvent(string Kind, string Origin, long Room = 0, long User = 0, long Message = 0, string? Action = null, string? Target = null, string? Html = null, string? Stream = null, string? ClientId = null, bool Flag = false, System.Text.Json.JsonElement? Payload = null);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CableControl))]
[JsonSerializable(typeof(CablePing))]
[JsonSerializable(typeof(CableMessage<string>))]
[JsonSerializable(typeof(CableMessage<ReadRoom>))]
[JsonSerializable(typeof(CableMessage<UnreadRoom>))]
[JsonSerializable(typeof(CableMessage<TypingNotification>))]
[JsonSerializable(typeof(CableMessage<System.Text.Json.JsonElement>))]
[JsonSerializable(typeof(RealtimeEvent))]
[JsonSerializable(typeof(string))]
internal partial class RealtimeJson : JsonSerializerContext;
