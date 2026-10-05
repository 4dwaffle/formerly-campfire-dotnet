using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Campfire.Features.Identity;

public sealed record IdentityErrorResponse(string error);

[JsonSerializable(typeof(IdentityErrorResponse))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(long))]
public partial class IdentityJsonContext : JsonSerializerContext
{
    // Rails signs serialized bytes. Keep the existing Unicode/HTML encoding
    // policy while replacing runtime object metadata with generated metadata.
    internal static IdentityJsonContext Relaxed { get; } = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}
