using System.Text.Json;
using System.Text.Json.Serialization;

namespace Campfire.Features.Persistence;

/// <summary>Passes ID sets as one scalar SQLite parameter without runtime list expansion.</summary>
public static class SqlParameters
{
    public static string Ids(IEnumerable<long> values) => JsonSerializer.Serialize(values.ToArray(), SqlParameterJsonContext.Default.Int64Array);

    public static List<T> Materialize<T>(this IEnumerable<T> values) => values as List<T> ?? values.ToList();
}

[JsonSerializable(typeof(long[]))]
internal partial class SqlParameterJsonContext : JsonSerializerContext;
