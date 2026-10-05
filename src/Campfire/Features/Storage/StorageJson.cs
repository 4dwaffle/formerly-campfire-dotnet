using System.Text.Json;
using System.Text.Json.Serialization;

namespace Campfire.Features.Storage;

internal sealed record StorageVariation(string format, int[] resize_to_limit);
internal sealed record DiskUploadToken(string key, string? content_type, long content_length, string checksum, string service_name);
internal sealed record DiskReadToken(string key, string disposition, string? content_type, string service_name);
internal sealed record UploadTarget(string url, Dictionary<string, string> headers);
internal sealed record DirectUploadResponse(long id, string key, string filename, string? content_type, JsonElement metadata, string service_name, long byte_size, string checksum, string created_at, string signed_id, UploadTarget direct_upload);

[JsonSerializable(typeof(StorageVariation))]
[JsonSerializable(typeof(DiskUploadToken))]
[JsonSerializable(typeof(DiskReadToken))]
[JsonSerializable(typeof(DirectUploadResponse))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(int[]))]
internal partial class StorageJson : JsonSerializerContext;
