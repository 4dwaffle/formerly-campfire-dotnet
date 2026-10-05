using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CampfireParity;

public static class ExportRoutes
{
    public static void Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var output = Path.GetFullPath(args[1]);
        var temporary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "campfire-parity-routes-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(temporary);
        try
        {
            using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(Path.Combine(root, "src", "Campfire"));
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CAMPFIRE_STORAGE"] = temporary,
                    ["SECRET_KEY_BASE"] = new string('a', 128),
                    ["CAMPFIRE_DELIVER_INTEGRATIONS"] = "false"
                }));
            });
            using var client = factory.CreateClient();
            var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Select(endpoint => new
                {
                    path = endpoint.RoutePattern.RawText,
                    methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
                    name = endpoint.DisplayName
                }).ToArray();
            File.WriteAllText(output, JsonSerializer.Serialize(endpoints, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Exported {endpoints.Length} actual mapped route endpoints");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!temporary.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(temporary).StartsWith("campfire-parity-routes-"))
                throw new InvalidOperationException("Temporary cleanup path escaped its declared root");
            Directory.Delete(temporary, true);
        }
    }
}
