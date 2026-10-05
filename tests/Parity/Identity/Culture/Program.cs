using System.Globalization;
using System.Text.Json.Nodes;
using Campfire.Features.Identity;
using Microsoft.Extensions.Configuration;

var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "rails_compat.json")))!;
var vector = vectors["signed_cookies"]!["generate"]!.AsArray().First(v => v!["expires_at"] != null)!;
var secret = vectors["secret_key_base"]!.GetValue<string>();
var crypto = new RailsCrypto(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SECRET_KEY_BASE"] = secret }).Build());
var expiry = DateTimeOffset.Parse(vector["expires_at"]!.GetValue<string>(), CultureInfo.InvariantCulture);
var expected = vector["raw"]!.GetValue<string>();
var failures = 0;
foreach (var culture in new[] { "en-US", "cs-CZ", "tr-TR", "th-TH", "ar-SA" })
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
    CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
    var actual = crypto.SignCookie(vector["name"]!.GetValue<string>(), vector["value"]!.GetValue<string>(), expiry);
    var match = actual == expected;
    Console.WriteLine($"{culture}: Rails expiring signed-cookie golden vector matches = {match}");
    if (!match) failures++;
}
return failures == 0 ? 0 : 1;
