using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Primitives;

namespace Campfire.Features.Identity;

// Rack/ActionController parameter semantics used by the identity controllers.
// Keep JSON nodes rather than reflection-based object deserialization for AOT.
internal sealed class IdentityParameters
{
    private readonly JsonObject values;
    public IFormCollection Form { get; }
    private IdentityParameters(JsonObject values, IFormCollection form) { this.values = values; Form = form; }
    public static async Task<IdentityParameters> Read(HttpRequest request)
    {
        var form = request.HasFormContentType ? await request.ReadFormAsync(request.HttpContext.RequestAborted) : new FormCollection(new Dictionary<string, StringValues>());
        var values = new JsonObject();
        foreach (var pair in request.Query) Set(values, pair.Key, pair.Value.LastOrDefault());
        if (request.HasJsonContentType())
        {
            try
            {
                var body = await JsonNode.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted) as JsonObject ?? throw new BadHttpRequestException("Expected a JSON object.");
                foreach (var pair in body) values[pair.Key] = pair.Value?.DeepClone();
            }
            catch (JsonException) { throw new BadHttpRequestException("Invalid JSON."); }
        }
        else foreach (var pair in form) Set(values, pair.Key, pair.Value.LastOrDefault());
        return new(values, form);
    }
    private static void Set(JsonObject target, string name, string? value)
    {
        var parts = name.Replace("]", "", StringComparison.Ordinal).Split('[');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (target[parts[i]] is not JsonObject child) target[parts[i]] = child = new();
            target = child;
        }
        target[parts[^1]] = value;
    }
    public void Require(string name)
    {
        if (Form.Files.Any(f => f.Name.StartsWith(name + "[", StringComparison.Ordinal))) return;
        var node = values[name];
        if (node is JsonObject obj && obj.Count > 0) return;
        if (node == null || node is JsonObject empty && empty.Count == 0 || node is JsonArray array && array.Count == 0 || node is JsonValue text && text.TryGetValue<string>(out var value) && string.IsNullOrWhiteSpace(value))
            throw new BadHttpRequestException("param is missing or the value is empty: " + name);
        // Rails require accepts nonblank values (including false); the controller's
        // subsequent permit call then raises for values that are not Parameters.
        throw new InvalidOperationException("The required parameter does not support permit: " + name);
    }
    public JsonNode? Node(string name)
    {
        JsonNode? current = values;
        foreach (var part in name.Replace("]", "", StringComparison.Ordinal).Split('[')) current = current is JsonObject obj ? obj[part] : null;
        return current;
    }
    public string? Value(string name, string? fallback = null)
    {
        var node = Node(name);
        if (node == null) return fallback;
        if (node is not JsonValue scalar) return fallback; // permit does not accept arrays/objects as scalar attributes
        if (scalar.TryGetValue<string>(out var text)) return text;
        if (scalar.TryGetValue<bool>(out var boolean)) return boolean ? "true" : "false";
        return scalar.ToJsonString();
    }
    public bool Has(string name)
    {
        var parts = name.Replace("]", "", StringComparison.Ordinal).Split('[');
        JsonNode? parent = values;
        foreach (var part in parts[..^1]) parent = parent is JsonObject obj ? obj[part] : null;
        return parent is JsonObject container && container.TryGetPropertyValue(parts[^1], out var node) && (node == null || node is JsonValue);
    }
    public string? StringAttribute(string name, string? fallback = null)
    {
        if (Node(name) is JsonValue scalar && scalar.TryGetValue<bool>(out var boolean)) return boolean ? "t" : "f";
        return Value(name, fallback);
    }
    public string? Password(string name)
    {
        if (Node(name) is JsonValue scalar && !scalar.TryGetValue<string>(out _)) throw new InvalidOperationException("Password must be a string.");
        return Value(name);
    }
    public bool IsFalse(string name) => Node(name) is JsonValue scalar && scalar.TryGetValue<bool>(out var value) && !value;
    // ActiveModel::Type::Boolean::FALSE_VALUES, including symbols via string form.
    public static bool? Boolean(string? value) => value == null || value.Length == 0 ? null : value is not ("0" or "f" or "F" or "false" or "FALSE" or "off" or "OFF");
    public Dictionary<string, string?> Settings()
    {
        if (Node("account[settings]") is not JsonObject settings) return [];
        var result = new Dictionary<string, string?>();
        foreach (var pair in settings)
        {
            if (pair.Key != "restrict_room_creation_to_administrators") throw new InvalidOperationException("Unknown account setting.");
            result[pair.Key] = Value("account[settings][" + pair.Key + "]");
        }
        return result;
    }
}
