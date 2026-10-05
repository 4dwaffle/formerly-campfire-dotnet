// Generated from pinned upstream/app/views/pwa; regenerate with bench/parity/chat/generate_pwa.py.
using System.Net;
using System.Text;
using Campfire.Contracts;

namespace Campfire.Features.Chat;

public static class PwaInstructions
{
    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static string Image(IPageRenderer renderer, string logical, int size, string? alt, string? css) => $"<img src=\"{E(renderer.Asset(logical))}\" width=\"{size}\" height=\"{size}\"{(alt is null ? " aria-hidden=\"true\"" : " alt=\"" + E(alt) + "\"")}{(css is null ? "" : " class=\"" + E(css) + "\"")}>";
    private sealed class Platform
    {
        public Platform(string ua)
        {
            Ios = ua.Contains("iPhone") || ua.Contains("iPad") || ua.Contains("iPod") || ua.Contains("Macintosh") && ua.Contains("Mobile");
            Android = ua.Contains("Android"); Windows = ua.Contains("Windows");
            Edge = ua.Contains("Edg/") || ua.Contains("EdgA/") || ua.Contains("EdgiOS/");
            Firefox = ua.Contains("Firefox/") || ua.Contains("FxiOS/");
            Chrome = !Edge && (ua.Contains("Chrome/") || ua.Contains("CriOS/"));
            Safari = !Edge && !Firefox && !Chrome && ua.Contains("Safari/");
            Desktop = !Ios && !Android;
            Browser = Edge ? "Edge" : Firefox ? "Firefox" : Chrome ? "Chrome" : Safari ? "Safari" : "Unknown";
            OperatingSystem = Ios ? "iOS" : Android ? "Android" : Windows ? "Windows" : ua.Contains("Mac") ? "macOS" : ua.Contains("Linux") ? "Linux" : "Unknown";
        }
        public bool Ios { get; } public bool Android { get; } public bool Windows { get; } public bool Edge { get; }
        public bool Firefox { get; } public bool Chrome { get; } public bool Safari { get; } public bool Desktop { get; }
        public string Browser { get; } public string OperatingSystem { get; }
    }
    public static string Browser(HttpContext context, IPageRenderer renderer)
    {
        var platform = new Platform(context.Request.Headers.UserAgent.ToString());
        var html = new StringBuilder();
        if (!((platform.Safari || platform.Chrome) && platform.Ios))
        {
        html.Append("\n  <details class=\"notifications-help\" data-notifications-target=\"details\">\n    <summary class=\"btn\">\n      ");
        html.Append(Image(renderer,"external/web.svg",20,null,null));
        html.Append("\n      <strong>Check your ");
        html.Append(E(platform.Browser));
        html.Append(" settings</strong>\n      ");
        html.Append(Image(renderer,"disclosure.svg",10,null,"disclosure"));
        html.Append("\n    </summary>\n\n    ");
        if (platform.Firefox && platform.Android)
        {
        html.Append("\n        <ol>\n          <li>Tap <em>");
        html.Append(Image(renderer,"lock.svg",20,"the View site information button",null));
        html.Append("</em> in the address bar.</li>\n          <li>Tap <em>Notification</em> to change to <em>Allowed</em>.</li>\n        </ol>\n      ");
        }
        else if (platform.Edge && platform.Desktop)
        {
        html.Append("\n        <h2 class=\"txt-normal txt-medium margin-block-start\">Turn on notifications for this website.</h2>\n        <ol>\n          <li>Click <em>");
        html.Append(Image(renderer,"lock.svg",20,"the View site information button",null));
        html.Append("</em> left of the address bar.</li>\n          <li>Under <em>Permissions for this site &gt; Notifications</em>, choose <em>Allow</em>.</li>\n        </ol>\n        <h2 class=\"txt-normal txt-medium margin-block-start\">Turn on notifications for ");
        html.Append(E(platform.Browser));
        html.Append(".</h2>\n        <ol>\n          ");
        if (platform.Windows)
        {
        html.Append("\n            <li>Click <em>Start</em>, then <em>Settings</em>.</li>\n            <li>Go to <em>System &gt; Notification</em>.</li>\n            <li>Click <em>");
        html.Append(Image(renderer,"external/switch.svg",22,"the switch",null));
        html.Append("</em> <em>ON</em> for ");
        html.Append(E(platform.Browser));
        html.Append(".</li>\n          ");
        }
        else
        {
        html.Append("\n            <li>Click <em aria-label=\"the Apple menu\">\uf8ff</em> in the top left.</li>\n            <li>Click <em>System Settings\u2026</em>.</li>\n            <li>Click <em>Notifications</em>.</li>\n            <li>Click <em>");
        html.Append(E(platform.Browser));
        html.Append("</em>.</li>\n            <li>Click <em>");
        html.Append(Image(renderer,"external/switch.svg",22,"the switch",null));
        html.Append("</em> to <em>Allow notifications</em>.</li>\n          ");
        }
        html.Append("\n        </ol>\n      ");
        }
        else if (platform.Firefox && platform.Desktop)
        {
        html.Append("\n        <h2 class=\"txt-normal txt-medium margin-block-start\">Turn on notifications for this website.</h2>\n        <ol>\n          <li>Click <em>");
        html.Append(E(platform.Browser));
        html.Append("</em> in the top left.</li>\n          <li>Click <em>Settings\u2026</em>.</li>\n          <li>Click <em>Privacy & Security</em> in the sidebar.</li>\n          <li>Scroll down to <em>Permissions</em>.</li>\n          <li>Click <em>Settings</em> next to <em>Notifications</em>.</li>\n          <li>Select <em>Allow</em> next to <em>");
        html.Append(E(context.Request.Scheme + "://" + context.Request.Host + context.Request.PathBase + "/"));
        html.Append("</em>.</li>\n        </ol>\n\n        <h2 class=\"txt-normal txt-medium margin-block-start\">Turn on notifications for ");
        html.Append(E(platform.Browser));
        html.Append(".</h2>\n        <ol>\n          ");
        if (platform.Windows)
        {
        html.Append("\n            <li>Click <em>Start</em>, then <em>Settings</em>.</li>\n            <li>Go to <em>System &gt; Notification</em>.</li>\n            <li>Click <em>");
        html.Append(Image(renderer,"external/switch.svg",22,"the toggle button",null));
        html.Append("</em> <em>ON</em> for ");
        html.Append(E(platform.Browser));
        html.Append(".</li>\n          ");
        }
        else
        {
        html.Append("\n            <li>Click <em aria-label=\"the Apple menu\">\uf8ff</em> in the top left.</li>\n            <li>Click <em>System Settings\u2026</em>.</li>\n            <li>Click <em>Notifications</em>.</li>\n            <li>Click <em>");
        html.Append(E(platform.Browser));
        html.Append("</em>.</li>\n            <li>Click <em>");
        html.Append(Image(renderer,"external/switch.svg",22,"the switch",null));
        html.Append("</em> to <em>Allow notifications</em>.</li>\n          ");
        }
        html.Append("\n        </ol>\n      ");
        }
        else if (platform.Chrome && platform.Desktop)
        {
        html.Append("\n        <h2 class=\"txt-normal txt-medium margin-block-start\">Turn on notifications for this website.</h2>\n        <ol>\n          <li>Click the <em>");
        html.Append(Image(renderer,"external/sliders.svg",20,"View site information",null));
        html.Append("</em> icon in the address bar.</li>\n          <li>Click <em>Site Settings</em>.</li>\n          <li>Ensure notifications are <em>Allowed</em>.</li>\n        </ol>\n\n        <h2 class=\"txt-normal txt-medium margin-block-start\">Turn on notifications for ");
        html.Append(E(platform.Browser));
        html.Append(".</h2>\n        <ol>\n          ");
        if (platform.Windows)
        {
        html.Append("\n            <li>Click <em>Start</em>, then <em>Settings</em>.</li>\n            <li>Go to <em>System &gt; Notification</em>.</li>\n            <li>Click <em>");
        html.Append(Image(renderer,"external/switch.svg",22,"the switch",null));
        html.Append("</em> <em>ON</em> for ");
        html.Append(E(platform.Browser));
        html.Append(".</li>\n          ");
        }
        else
        {
        html.Append("\n            <li>Click <em aria-label=\"the Apple menu\">\uf8ff</em> in the top left.</li>\n            <li>Click <em>System Settings\u2026</em>.</li>\n            <li>Click <em>Notifications</em>.</li>\n            <li>Click <em>");
        html.Append(E(platform.Browser));
        html.Append("</em>.</li>\n            <li>Click <em>");
        html.Append(Image(renderer,"external/switch.svg",22,"the switch",null));
        html.Append("</em> to <em>Allow notifications</em>.</li>\n          ");
        }
        html.Append("\n        </ol>\n      ");
        }
        else if (platform.Chrome && platform.Android)
        {
        html.Append("\n        <ol>\n          <li>Tap the <em>");
        html.Append(Image(renderer,"menu-dots-vertical.svg",16,"More options",null));
        html.Append("</em> menu button.</li>\n          <li>Tap <em>Settings</em>.</li>\n          <li>Tap <em>Notifications</em>.</li>\n          <li>Tap <em>");
        html.Append(Image(renderer,"external/switch.svg",22,"the switch",null));
        html.Append("</em> to <em>Allow ");
        html.Append(E(platform.Browser));
        html.Append(" notifications</em>.</li>\n          <li>Tap <em>");
        html.Append(Image(renderer,"external/switch.svg",22,"the switch",null));
        html.Append("</em> next to <em>Web apps</em>.</li>\n          <li>Tap <em>");
        html.Append(Image(renderer,"notification-bell-alert.svg",16,"the notification bell",null));
        html.Append("</em> and select <em>Allow</em>.</li>\n        </ol>\n      ");
        }
        else if (platform.Safari && platform.Desktop)
        {
        html.Append("\n        <ol>\n          <li>Click <em>");
        html.Append(E(platform.Browser));
        html.Append("</em> in the top left.</li>\n          <li>Click <em>Settings\u2026</em>.</li>\n          <li>Click the <em>Websites</em> tab.</li>\n          <li>Click <em>Notifications</em> in the sidebar.</li>\n          <li>Click <em>");
        html.Append(E(context.Request.Scheme + "://" + context.Request.Host + context.Request.PathBase + "/"));
        html.Append("</em> in the list.</li>\n          <li>Select <em>Allow</em>.</li>\n        </ol>\n      ");
        }
        else
        {
        html.Append("\n        <p>Ensure notifications are enabled for <em>");
        html.Append(E(context.Request.Scheme + "://" + context.Request.Host + context.Request.PathBase + "/"));
        html.Append("</em> in your web browser settings.</p>\n    ");
        }
        html.Append("\n  </details>\n");
        }
        html.Append("\n");
        return html.ToString();
    }

    public static string System(HttpContext context, IPageRenderer renderer)
    {
        var platform = new Platform(context.Request.Headers.UserAgent.ToString());
        var html = new StringBuilder();
        html.Append("<details class=\"notifications-help hide-in-browser\" data-notifications-target=\"details\">\n  <summary class=\"btn\">\n    ");
        html.Append(Image(renderer,"external/gear.svg",20,null,null));
        html.Append("\n    <strong>Check your ");
        html.Append(E(platform.OperatingSystem));
        html.Append(" settings</strong>\n    ");
        html.Append(Image(renderer,"disclosure.svg",10,null,"disclosure"));
        html.Append("\n  </summary>\n\n  ");
        if (platform.Firefox && platform.Android)
        {
        html.Append("\n      <ol>\n        <li>Tap the <em>");
        html.Append(Image(renderer,"menu-dots-vertical.svg",16,"More options",null));
        html.Append("</em> menu button.</li>\n        <li>Tap <em>Settings</em>.</li>\n        <li>Tap <em>Notifications</em>.</li>\n        <li>Tap <em>");
        html.Append(Image(renderer,"external/switch.svg",22,"the toggle button",null));
        html.Append("</em> to <em>Allow ");
        html.Append(E(platform.Browser));
        html.Append(" notifications</em>.</li>\n      </ol>\n    ");
        }
        else if (platform.Edge && platform.Desktop)
        {
        html.Append("\n      <ol>\n        <li>Click <em>Start</em>, then <em>Settings</em>.</li>\n        <li>Go to <em>System &gt; Notification</em>.</li>\n        <li>Click <em>");
        html.Append(Image(renderer,"external/switch.svg",22,"the toggle button",null));
        html.Append("</em> <em>ON</em> for Campfire.</li>\n      </ol>\n    ");
        }
        else if ((platform.Firefox || platform.Chrome) && platform.Desktop)
        {
        html.Append("\n      <ol>\n        ");
        if (platform.Windows)
        {
        html.Append("\n          <li>Click <em>Start</em>, then <em>Settings</em>.</li>\n          <li>Go to <em>System &gt; Notification</em>.</li>\n          <li>Click <em>");
        html.Append(Image(renderer,"external/switch.svg",22,"the toggle button",null));
        html.Append("</em> <em>ON</em> for Campfire.</li>\n        ");
        }
        else
        {
        html.Append("\n          <li>Click <em aria-label=\"the Apple menu\">\uf8ff</em> in the top left.</li>\n          <li>Click <em>System Settings\u2026</em>.</li>\n          <li>Click <em>Notifications</em>.</li>\n          <li>Click <em>Campfire</em>.</li>\n          <li>Click <em>");
        html.Append(Image(renderer,"external/switch.svg",22,"the allow notifications switch",null));
        html.Append("</em> to <em>Allow notifications</em>.</li>\n        ");
        }
        html.Append("\n      </ol>\n    ");
        }
        else if (platform.Safari && platform.Desktop)
        {
        html.Append("\n      <ol>\n        <li>Click <em aria-label=\"the Apple menu\">\uf8ff</em> in the top left.</li>\n        <li>Click <em>System Settings\u2026</em>.</li>\n        <li>Click <em>Notifications</em>.</li>\n        <li>Click <em>Campfire</em>.</li>\n        <li>Click <em>");
        html.Append(Image(renderer,"external/switch.svg",22,"the allow notifications switch",null));
        html.Append("</em> to <em>Allow notifications</em>.</li>\n      </ol>\n    ");
        }
        else if ((platform.Safari || platform.Chrome) && platform.Ios)
        {
        html.Append("\n      <ol>\n        <li>Open the <em>");
        html.Append(Image(renderer,"external/gear.svg",20,null,null));
        html.Append("</em> Settings app.</li>\n        <li>Scroll to and tap <em>Campfire</em>.</li>\n        <li>Tap <em>Notifications</em>.</li>\n        <li>Tap <em>");
        html.Append(Image(renderer,"external/switch.svg",22,"the allow notifications switch button",null));
        html.Append("</em> to <em>Allow Notifications</em>.</li>\n      </ol>\n    ");
        }
        else if (platform.Chrome && platform.Android)
        {
        html.Append("\n      <ol>\n        <li>Open the <em>");
        html.Append(Image(renderer,"external/gear.svg",20,null,null));
        html.Append("</em> Settings app.</li>\n        <li>Tap <em>Notifications</em>.</li>\n        <li>Tap <em>App notifications</em>.</li>\n        <li>Scroll to <em>Campfire</em>.</li>\n        <li>Tap <em>");
        html.Append(Image(renderer,"external/switch.svg",22,"the switch",null));
        html.Append("</em> to <em>Allow Notifications</em>.</li>\n      </ol>\n    ");
        }
        else
        {
        html.Append("\n      <p>Ensure notifications are allowed for ");
        html.Append(E(platform.Browser));
        html.Append(" in your system settings.</p>\n  ");
        }
        html.Append("\n</details>\n");
        return html.ToString();
    }

    public static string Install(HttpContext context, IPageRenderer renderer)
    {
        var platform = new Platform(context.Request.Headers.UserAgent.ToString());
        var html = new StringBuilder();
        if (!(platform.Chrome || (platform.Firefox && !platform.Android)))
        {
        html.Append("\n  <details class=\"notifications-help pwa__instructions hide-in-pwa\" data-controller=\"pwa-install\" data-pwa-install-prompting-class=\"pwa--can-install\" data-notifications-target=\"details\">\n    <summary class=\"btn\">\n      ");
        html.Append(Image(renderer,"external/install.svg",20,null,null));
        html.Append("\n      <strong>Install Campfire as a web app.</strong>\n      ");
        html.Append(Image(renderer,"disclosure.svg",10,null,"disclosure"));
        html.Append("\n    </summary>\n\n    ");
        if (platform.Edge)
        {
        html.Append("\n        <ol>\n          <li>Click <em>");
        html.Append(Image(renderer,"install-edge.svg",16,"the app available - install Campfire chat button",null));
        html.Append("</em>in the address bar.</li>\n          <li>Click <em>Install</em>.</li>\n        </ol>\n      ");
        }
        else if (platform.Chrome && platform.Android)
        {
        html.Append("\n        <ol>\n          <li>Tap the <em>");
        html.Append(Image(renderer,"menu-dots-vertical.svg",16,"More options",null));
        html.Append("</em> menu button.</li>\n          <li>Tap <em>Install app</em> in the menu.</li>\n        </ol>\n      ");
        }
        else if (platform.Firefox && platform.Android)
        {
        html.Append("\n        <ol>\n          <li>Tap the <em>");
        html.Append(Image(renderer,"menu-dots-vertical.svg",16,"More options",null));
        html.Append("</em> menu button.</li>\n          <li>Tap <em>Install</em> in the menu.</li>\n        </ol>\n      ");
        }
        else if (platform.Safari && platform.Desktop)
        {
        html.Append("\n        <ol>\n          <li>Click <em>File</em> in the top left.</li>\n          <li>Click <em>Add to Dock\u2026</em>.</li>\n        </ol>\n      ");
        }
        else if ((platform.Safari || platform.Chrome) && platform.Ios)
        {
        html.Append("\n        <p>To receive push notifications in ");
        html.Append(E(platform.Browser));
        html.Append(" for ");
        html.Append(E(platform.OperatingSystem));
        html.Append(", you must install Campfire as a web app.</p>\n        <ol>\n          <li>Tap <em>");
        html.Append(Image(renderer,"external/share.svg",20,"the share button",null));
        html.Append("</em></li>\n          <li>Tap <em>Add to Home Screen</em>.</li>\n        </ol>\n      ");
        }
        else
        {
        html.Append("\n        <p>Some platforms require you to install Campfire as a web app to receive push notifications.</p>\n    ");
        }
        html.Append("\n\n    <div class=\"margin-block-start txt-align-center pwa__installer\">\n      <hr class=\"separator margin-block\">\n      <button class=\"btn btn--reversed center\" data-action=\"pwa-install#promptInstall\">\n        ");
        html.Append(Image(renderer,"external/install.svg",20,null,null));
        html.Append("\n        Install now\n      </button>\n    </div>\n  </details>\n");
        }
        html.Append("\n");
        return html.ToString();
    }
}
