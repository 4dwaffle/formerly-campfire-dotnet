using System.Text.Json.Serialization;

namespace Campfire.Features.Chat;

public sealed record AutocompleteUserResponse(string name, long value, string avatar_url, string sgid);
public sealed record ChatUserResponse(long id, string name, string role, string avatar_url);
public sealed record MessageBodyResponse(string plain_text, string html);
public sealed record MessageRoomResponse(long id);
public sealed record MessageResponse(long id, string created_at, MessageBodyResponse body, ChatUserResponse creator, MessageRoomResponse room, string url);
public sealed record BoostMessageResponse(long id, string url);
public sealed record BoostResponse(long id, string content, string created_at, ChatUserResponse booster, BoostMessageResponse message);
public sealed record ChatErrorResponse(int status, string error);

[JsonSerializable(typeof(AutocompleteUserResponse[]))]
[JsonSerializable(typeof(MessageResponse))]
[JsonSerializable(typeof(MessageResponse[]))]
[JsonSerializable(typeof(BoostResponse))]
[JsonSerializable(typeof(ChatErrorResponse))]
public partial class ChatJsonContext : JsonSerializerContext;
