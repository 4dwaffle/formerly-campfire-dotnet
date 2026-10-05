using System.Text.Json.Serialization;

namespace Campfire.Features.WebSupport;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WebManifest))]
internal partial class WebSupportJsonContext : JsonSerializerContext;

internal sealed class WebManifest
{
    public string name { get; init; } = "";
    public ManifestIcon[] icons { get; init; } = [];
    public string start_url { get; init; } = "/";
    public string display { get; init; } = "standalone";
    public string scope { get; init; } = "/";
    public string description { get; init; } = "";
    public string[] categories { get; init; } = [];
    public string theme_color { get; init; } = "";
    public string background_color { get; init; } = "";
    public ManifestShortcut[] shortcuts { get; init; } = [];
    public ManifestScreenshot[] screenshots { get; init; } = [];
}

internal sealed class ManifestIcon
{
    public string src { get; init; } = "";
    public string type { get; init; } = "";
    public string sizes { get; init; } = "";
    public string? purpose { get; init; }
}

internal sealed class ShortcutIcon
{
    public string src { get; init; } = "";
    public string sizes { get; init; } = "";
}

internal sealed class ManifestShortcut
{
    public string name { get; init; } = "";
    public string description { get; init; } = "";
    public string url { get; init; } = "";
    public ShortcutIcon[] icons { get; init; } = [];
}

internal sealed class ManifestScreenshot
{
    public string src { get; init; } = "";
    public string sizes { get; init; } = "";
    public string form_factor { get; init; } = "";
    public string label { get; init; } = "";
}
