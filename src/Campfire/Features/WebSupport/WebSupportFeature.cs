using Dapper;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Net;
using Campfire.Contracts;
using QRCoder;

namespace Campfire.Features.WebSupport;

public static class WebSupportFeature
{
        private static IResult RenderManifest(HttpContext context, IDataStore database, IWebHostEnvironment environment)
        {
            if (!Accepts(context, "json", "application/json")) return Results.StatusCode(406);
            var account = database.Read(queryConnection1 => queryConnection1.QuerySingleOrDefault<AccountInfo>("SELECT name,updated_at FROM accounts LIMIT 1"));
            var manifestPath = Path.Combine(environment.WebRootPath, "manifest.json");
            var manifest = new Dictionary<string, string>();
            if (File.Exists(manifestPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                foreach (var item in document.RootElement.EnumerateObject())
                    manifest[item.Name] = item.Value.GetProperty("digested_path").GetString() ?? item.Name;
            }
            string Asset(string name) => context.Request.Scheme + "://" + context.Request.Host + context.Request.PathBase + "/assets/" + manifest.GetValueOrDefault(name, name);
            var version = DateTime.TryParse(account?.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated) ? updated.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : "";
            var logo = "/account/logo?v=" + version;
            var value = new WebManifest
            {
                name = WebUtility.HtmlEncode(account?.Name ?? "Campfire"),
                icons = new ManifestIcon[]
                {
                    new() { src = "/account/logo?size=small&amp;v=" + version, type = "image/png", sizes = "192x192" },
                    new() { src = logo, type = "image/png", sizes = "512x512" },
                    new() { src = logo, type = "image/png", sizes = "512x512", purpose = "maskable" }
                },
                start_url = "/", display = "standalone", scope = "/",
                description = "A chat app from the makers of Basecamp and HEY.",
                categories = new[] { "social", "business", "productivity" },
                theme_color = "#ffffff", background_color = "#ffffff",
                shortcuts = new ManifestShortcut[]
                {
                    new() { name = "New chat room", description = "Open Campfire and start a new chat room", url = "rooms/opens/new", icons = new ShortcutIcon[] { new() { src = Asset("add.svg"), sizes = "any" } } },
                    new() { name = "My profile", description = "Open Campfire and view your profile", url = "/users/me/profile", icons = new ShortcutIcon[] { new() { src = Asset("person.svg"), sizes = "any" } } }
                },
                screenshots = new ManifestScreenshot[]
                {
                    new() { src = Asset("screenshots/android-chat.png"), sizes = "1080x2400", form_factor = "narrow", label = "Campfire is an installable, self-hosted group chat system." },
                    new() { src = Asset("screenshots/android-sidebar.png"), sizes = "1080x2400", form_factor = "narrow", label = "Easily invite people. Make rooms. @mentions, DMs, and mobile support." },
                    new() { src = Asset("screenshots/android-dark-mode.png"), sizes = "1080x2400", form_factor = "narrow", label = "Full support for dark mode, customizable to your brand." }
                }
            };
            return Results.Json(value, WebSupportJsonContext.Default.WebManifest, contentType: "application/json");
        }
    public static WebApplication MapWebSupportFeature(this WebApplication app)
    {
        app.MapGet("/webmanifest", RenderManifest);
        app.MapGet("/webmanifest.json", RenderManifest);
        app.MapGet("/service-worker", (HttpContext context) => Accepts(context, "js", "text/javascript") ? Results.Text(ServiceWorker, "text/javascript; charset=utf-8") : Results.StatusCode(406));
        app.MapGet("/qr_code/{id}", (HttpContext context, string id) =>
        {
            try
            {
                var encoded = id.Replace('-', '+').Replace('_', '/');
                if (encoded.Any(char.IsWhiteSpace)) throw new FormatException("Invalid Base64 QR payload.");
                var payload = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
                using var code = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.H);
                using var svg = new SvgQRCode(code);
                context.Response.Headers.CacheControl = "public, max-age=31536000";
                return Results.Text(svg.GetGraphic(20, "#000000", "#ffffff", true, SvgQRCode.SizingMode.ViewBoxAttribute), "image/svg+xml");
            }
            catch (Exception error) when (error is FormatException or QRCoder.Exceptions.DataTooLongException)
            {
                // Rails lets invalid/oversized decoded payloads reach its
                // exception boundary, which serves the production error page.
                throw;
            }
        });
        return app;
    }

    private static bool Accepts(HttpContext context, string format, string mime)
    {
        if (context.Items["RailsFormat"] is string suffix) return suffix == format;
        var accepted = context.Request.Headers.Accept.ToString();
        return string.IsNullOrEmpty(accepted) || accepted.Contains("*/*", StringComparison.Ordinal) || accepted.Contains(mime, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class AccountInfo
    {
        public string Name { get; set; } = "Campfire";
        public string UpdatedAt { get; set; } = "";
    }

    // Kept equivalent to upstream/app/views/pwa/service_worker.js.
    private const string ServiceWorker = """
        self.addEventListener("push", async (event) => {
          const data = await event.data.json()
          event.waitUntil(Promise.all([ showNotification(data), updateBadgeCount(data.options) ]))
        })
        async function showNotification({ title, options }) {
          return self.registration.showNotification(title, options)
        }
        async function updateBadgeCount({ data: { badge } }) {
          return self.navigator.setAppBadge?.(badge || 0)
        }
        self.addEventListener("notificationclick", (event) => {
          event.notification.close()
          const url = new URL(event.notification.data.path, self.location.origin).href
          event.waitUntil(openURL(url))
        })
        async function openURL(url) {
          const clients = await self.clients.matchAll({ type: "window" })
          const focused = clients.find((client) => client.focused)
          if (focused) {
            await focused.navigate(url)
          } else {
            await self.clients.openWindow(url)
          }
        }
        """;
}
