using Dapper;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Campfire.Contracts;
using Ganss.Xss;

namespace Campfire.Features.Chat;

/// <summary>Safe Action Text content; never render client HTML without this boundary.</summary>
public sealed partial class RichText(IDataStore db, IRailsCrypto crypto, IHttpContextAccessor accessor)
{
    private static readonly ConcurrentBag<HtmlSanitizer> sanitizers = new();
    private static int sanitizerCount;
    private static readonly int sanitizerLimit = Math.Max(1, Environment.ProcessorCount);
    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        foreach (var tag in "a abbr acronym address b big blockquote br cite code dd del dfn div dl dt em h1 h2 h3 h4 h5 h6 hr i ins kbd li ol p pre samp small span strong sub sup time tt ul var s u mark table thead tbody tfoot tr th td action-text-attachment figure figcaption".Split(' ')) sanitizer.AllowedTags.Add(tag);
        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in "href title class cite datetime colspan rowspan start reversed type data-language sgid content-type caption filename filesize width height presentation content url".Split(' ')) sanitizer.AllowedAttributes.Add(attribute);
        sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto" }) sanitizer.AllowedSchemes.Add(scheme);
        sanitizer.KeepChildNodes = false;
        return sanitizer;
    }
    public string Sanitize(string? html)
    {
        var sanitizer = sanitizers.TryTake(out var reusable) ? reusable : CreateSanitizer();
        if (reusable is not null) Interlocked.Decrement(ref sanitizerCount);
        try { return sanitizer.Sanitize(html ?? ""); }
        finally
        {
            // A sanitizer has mutable parsing state. Borrow it exclusively and
            // return it only after completion; configuration never changes.
            if (Interlocked.Increment(ref sanitizerCount) <= sanitizerLimit) sanitizers.Add(sanitizer);
            else Interlocked.Decrement(ref sanitizerCount);
        }
    }
    public string PlainText(string? html)
    {
        // ActionText::Fragment.from_html calls Ruby String#strip before parsing;
        // its ASCII/NUL rules differ from .NET's Unicode Trim default.
        var trimmed = (html ?? "").Trim(' ', '\t', '\r', '\n', '\f', '\v', '\0');
        // A text-only HTML fragment needs neither tokenization nor the node
        // reducer. Entities, tags, NUL replacement and CR normalization must
        // still take the parser path; Unicode whitespace remains untouched.
        if (trimmed.AsSpan().IndexOfAny("<&\0\r") < 0) return ChompNewlines(trimmed);
        var document = new HtmlParser().ParseDocument("<html><body>" + trimmed + "</body></html>");
        foreach (var mention in document.QuerySelectorAll("action-text-attachment,span.mention"))
        {
            var user = MentionUser(mention.GetAttribute("sgid"));
            if (user is not null) mention.TextContent = "@" + user.Name;
            else if (mention.GetAttribute("content-type") == "application/vnd.actiontext.opengraph-embed") mention.TextContent = "";
            else if (!string.IsNullOrEmpty(mention.GetAttribute("caption"))) mention.TextContent = mention.GetAttribute("caption")!;
        }
        if (document.Body is null) return "";
        // Port ActionText::PlainTextConversion's bottom-up reducer directly:
        // plaintext is independent of the stricter presentation HTML filter.
        var pending = new Stack<INode>();
        var traversal = new List<INode>();
        pending.Push(document.Body);
        while (pending.TryPop(out var node))
        {
            traversal.Add(node);
            foreach (var child in node.ChildNodes) pending.Push(child);
        }
        var values = new Dictionary<INode, string>();
        for (var i = traversal.Count - 1; i >= 0; i--)
        {
            var node = traversal[i];
            var children = string.Concat(node.ChildNodes.Select(child => values[child]));
            var text = ChompNewlines(children);
            values[node] = node is IText ? ChompNewlines(node.TextContent) : node is IElement element ? element.LocalName switch
            {
                "script" or "style" => "",
                "p" or "h1" => text + "\n\n",
                "div" => text + "\n",
                "br" => "\n",
                "figcaption" => "[" + text + "]",
                "blockquote" => QuoteBlock(text),
                "ul" or "ol" => (ListDepth(element) > 0 ? "\n" : "") + text + "\n\n",
                "li" => ListItem(element, text),
                _ => children
            } : children;
        }
        return ChompNewlines(values[document.Body]);
    }
    private static string ChompNewlines(string text)
    {
        var end = text.Length;
        while (end > 0 && text[end - 1] == '\n') { end--; if (end > 0 && text[end - 1] == '\r') end--; }
        return text[..end];
    }
    private static int ListDepth(IElement element)
    {
        var depth = 0;
        for (var parent = element.ParentElement; parent is not null; parent = parent.ParentElement) if (parent.LocalName is "ul" or "ol") depth++;
        return depth;
    }
    private static string ListItem(IElement element, string text)
    {
        var list = element.ParentElement;
        while (list is not null && list.LocalName is not ("ul" or "ol")) list = list.ParentElement;
        var bullet = list?.LocalName == "ol" ? ((element.ParentElement?.Children.ToList().IndexOf(element) ?? 0) + 1).ToString(CultureInfo.InvariantCulture) + "." : "•";
        return new string(' ', Math.Max(0, ListDepth(element) - 1) * 2) + bullet + " " + text + "\n";
    }
    private static string QuoteBlock(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "“”";
        var first = 0;
        while (char.IsWhiteSpace(text[first])) first++;
        var last = text.Length - 1;
        while (char.IsWhiteSpace(text[last])) last--;
        return text[..first] + "“" + text[first..(last + 1)] + "”" + text[(last + 1)..] + "\n\n";
    }
    public string EditableBody(string body, Func<UserRecord, string> avatar)
    {
        var document = new HtmlParser().ParseDocument("<html><body>" + Sanitize(body) + "</body></html>");
        foreach (var node in document.QuerySelectorAll("action-text-attachment"))
        {
            var user = MentionUser(node.GetAttribute("sgid"));
            if (user is not null)
            {
                node.SetAttribute("sgid", MentionSgid(user.Id));
                node.SetAttribute("content-type", "application/vnd.campfire.mention");
                node.SetAttribute("content", MentionHtml(user, avatar));
            }
            else if (node.GetAttribute("content-type") == "application/vnd.actiontext.opengraph-embed")
                node.SetAttribute("content", Opengraph(node.GetAttribute("content"), node.GetAttribute("href"), node.GetAttribute("url"), node.GetAttribute("filename"), node.GetAttribute("caption")));
            else
            {
                // Unsupported handwritten attachments must not retain executable editor content.
                node.RemoveAttribute("content");
            }
        }
        return document.Body?.InnerHtml ?? "";
    }
    private string MentionHtml(UserRecord user, Func<UserRecord, string> avatar) => "<span class=\"mention\" sgid=\"" + WebUtility.HtmlEncode(MentionSgid(user.Id)) + "\">" + avatar(user) + " " + WebUtility.HtmlEncode(user.Name) + "</span>";
    public string Presentation(string body, Func<UserRecord, string> avatar) => Render(body, avatar, true);
    public string BodyHtml(string body, Func<UserRecord, string> avatar) => Render(body, avatar, false);
    private string Render(string body, Func<UserRecord, string> avatar, bool presentation)
    {
        var safe = Sanitize(body);
        var document = new HtmlParser().ParseDocument("<html><body>" + safe + "</body></html>");
        if (presentation) RemoveSoloUrl(document);
        foreach (var node in document.QuerySelectorAll("action-text-attachment,span.mention"))
        {
            if (node.GetAttribute("content-type") == "application/vnd.actiontext.opengraph-embed")
            {
                var embed = document.CreateElement("div");
                embed.InnerHtml = Opengraph(node.GetAttribute("content"), node.GetAttribute("href"), node.GetAttribute("url"), node.GetAttribute("filename"), node.GetAttribute("caption"));
                node.Replace(embed);
                continue;
            }
            var user = MentionUser(node.GetAttribute("sgid"));
            if (user is null) { node.Replace(document.CreateTextNode(node.GetAttribute("caption") ?? "")); continue; }
            var replacement = document.CreateElement("span");
            replacement.ClassName = "mention";
            replacement.SetAttribute("sgid", node.GetAttribute("sgid") ?? "");
            replacement.InnerHtml = avatar(user) + " " + WebUtility.HtmlEncode(user.Name);
            node.Replace(replacement);
        }
        // Auto-link only text nodes, never an attribute or an existing anchor.
        foreach (var element in presentation ? document.QuerySelectorAll("body,body *").ToArray() : [])
        {
            if (element.Closest("a") is not null) continue;
            foreach (var node in element.ChildNodes.Where(n => n.NodeType == AngleSharp.Dom.NodeType.Text).ToArray())
            {
                var text = node.TextContent;
                if (!UrlPattern().IsMatch(text) && !EmailPattern().IsMatch(text)) continue;
                var container = document.CreateElement("span");
                container.InnerHtml = AutoLink(text);
                node.Parent!.ReplaceChild(container, node);
            }
        }
        var html = document.Body?.InnerHtml ?? "";
        // AngleSharp emits '<' literally inside attributes (valid HTML, but keep
        // the HTTP escaping contract explicit for every user-provided value).
        html = AttributePattern().Replace(html, m => m.Groups[1].Value + WebUtility.HtmlEncode(WebUtility.HtmlDecode(m.Groups[2].Value)) + '"');
        return "<div class=\"lexxy-content\">" + html + "</div>";
    }
    private string Opengraph(string? content, string? href, string? image, string? title, string? description)
    {
        // Lexxy serializes the embed as an escaped content attribute; reconstruct
        // its known shape from metadata instead of trusting the embedded markup.
        if (!string.IsNullOrEmpty(content) && string.IsNullOrEmpty(title))
        {
            var fragment = new HtmlParser().ParseDocument(content);
            var titleNode = fragment.QuerySelector(".og-embed__title");
            var link = titleNode?.QuerySelector("a");
            href = link?.GetAttribute("href");
            title = (link ?? titleNode)?.TextContent.Trim();
            description = fragment.QuerySelector(".og-embed__description")?.TextContent;
            image = fragment.QuerySelector(".og-embed__image img")?.GetAttribute("src");
        }
        var imageHtml = SafeWebUrl(image) ? $"<div class=\"og-embed__image\"><img src=\"{WebUtility.HtmlEncode(image)}\" class=\"image center\" alt=\"\"></div>" : "";
        var avatarClass = image?.StartsWith("https://pbs.twimg.com/profile_images", StringComparison.Ordinal) == true ? " og-embed--twitter-avatar" : "";
        var titleHtml = SafeWebUrl(href) ? $"<a href=\"{WebUtility.HtmlEncode(href)}\">{WebUtility.HtmlEncode(title)}</a>" : WebUtility.HtmlEncode(title);
        return $"<div class=\"og-embed gap{avatarClass}\"><div class=\"og-embed__content\"><div class=\"og-embed__title\">{titleHtml}</div><div class=\"og-embed__description\">{WebUtility.HtmlEncode(description)}</div></div>{imageHtml}</div>";
    }
    public bool SafeWebUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        // Match the Rails raw-host policy before .NET/browser URI normalization.
        var authority = Regex.Match(value, @"\Ahttps?://([^/?#]+)", RegexOptions.IgnoreCase).Groups[1].Value;
        var host = authority.Split('@')[^1].Split(':')[0];
        if (host.Contains('%') || !host.Contains('.') || host.StartsWith('[')) return false;
        var ending = host.TrimEnd('.').Split('.')[^1];
        if (!Regex.IsMatch(ending, "[a-z]", RegexOptions.IgnoreCase) || ending.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;
        return !string.Equals(host.TrimEnd('.'), accessor.HttpContext?.Request.Host.Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }
    public string MentionSgid(long userId) => crypto.SignedGlobalId("User", userId, "attachable");
    public UserRecord? MentionUser(string? sgid)
    {
        if (string.IsNullOrEmpty(sgid)) return null;
        var id = crypto.VerifySignedGlobalId(sgid, "User", "attachable") ?? LegacyMentionId(sgid);
        return id.HasValue ? db.Read(queryConnection1 => queryConnection1.QuerySingleOrDefault<UserRecord>("select * from users where id=@id",new { id })) : null;
    }
    private static long? LegacyMentionId(string sgid)
    {
        // Only stored rich-text User mentions use this Rails compatibility fallback.
        // Never use it for identity, authorization, avatars or blob signatures.
        try
        {
            using var json = JsonDocument.Parse(DecodeBase64(sgid.Split("--")[0]));
            var rails = json.RootElement.GetProperty("_rails");
            string? gid = null;
            if (rails.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String) gid = data.GetString();
            else if (rails.TryGetProperty("message", out var message))
                gid = Regex.Match(Encoding.UTF8.GetString(DecodeBase64(message.GetString()!)), @"gid://campfire/[^/]+/\d+").Value;
            var match = Regex.Match(gid ?? "", @"\Agid://campfire/User/(\d+)(?:\?[^\s]*)?\z");
            return match.Success && long.TryParse(match.Groups[1].Value, out var id) ? id : null;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException) { return null; }
    }
    private static byte[] DecodeBase64(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
    private void RemoveSoloUrl(IDocument document)
    {
        var embeds = document.QuerySelectorAll("action-text-attachment[content-type='application/vnd.actiontext.opengraph-embed']");
        if (embeds.Length != 1) return;
        var node = embeds[0];
        var href = node.GetAttribute("href");
        if (string.IsNullOrEmpty(node.GetAttribute("filename"))) href = new HtmlParser().ParseDocument(node.GetAttribute("content") ?? "").QuerySelector(".og-embed__title a")?.GetAttribute("href");
        if (!SafeWebUrl(href) || NormalizeTweet(href!) != NormalizeTweet(PlainText(document.Body?.InnerHtml))) return;
        var divs = document.QuerySelectorAll("div");
        if (divs.Length != 0)
        {
            foreach (var div in divs) div.InnerHtml = node.OuterHtml;
        }
        else foreach (var paragraph in document.QuerySelectorAll("p").Where(p => p.QuerySelector("action-text-attachment") is null).ToArray()) paragraph.Remove();
    }
    private static string NormalizeTweet(string url) => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Host is "x.com" or "twitter.com" ? new UriBuilder(uri) { Host = "twitter.com", Query = "" }.Uri.AbsoluteUri : url;
    private static string AutoLink(string text)
    {
        var pattern = new Regex(@"(?:(?:ed2k|ftp|http|https|irc|mailto|news|gopher|nntp|telnet|webcal|xmpp|callto|feed|svn|urn|aim|rsync|tag|ssh|sftp|rtsp|afs|file)://|www\.\w)[^\s<\u00A0"" ]+|[\w.!#$%&'*+/=?^`{|}~-]+@[\w-]+(?:\.[\w-]+)+", RegexOptions.IgnoreCase);
        var result = new StringBuilder();
        var offset = 0;
        foreach (Match match in pattern.Matches(text))
        {
            result.Append(WebUtility.HtmlEncode(text[offset..match.Index]));
            var value = match.Value;
            var isUrl = value.Contains("://", StringComparison.Ordinal) || value.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
            if (isUrl)
            {
                while (value.Length > 0 && !Regex.IsMatch(value[^1..], @"[\p{L}\p{M}\p{N}\p{Pc}/\-=;]"))
                {
                    var closing = value[^1];
                    var opening = closing switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };
                    if (opening != '\0' && value.Count(c => c == opening) >= value.Count(c => c == closing)) break;
                    value = value[..^1];
                }
            }
            var href = isUrl ? value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value : "mailto:" + value;
            result.Append("<a target=\"_blank\" href=\"").Append(WebUtility.HtmlEncode(href)).Append("\">").Append(WebUtility.HtmlEncode(value)).Append("</a>").Append(WebUtility.HtmlEncode(match.Value[value.Length..]));
            offset = match.Index + match.Length;
        }
        return result.Append(WebUtility.HtmlEncode(text[offset..])).ToString();
    }
    public static long Epoch(string timestamp) => DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value.ToUnixTimeMilliseconds() : 0;
    public static string Iso(string timestamp) => DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) : timestamp;
    public static string Version(string timestamp) => DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : timestamp;
    [GeneratedRegex(@"(?:(?:ed2k|ftp|http|https|irc|mailto|news|gopher|nntp|telnet|webcal|xmpp|callto|feed|svn|urn|aim|rsync|tag|ssh|sftp|rtsp|afs|file)://|www\.\w)[^\s<\u00A0""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
    [GeneratedRegex(@"[\w.!#$%&'*+/=?^`{|}~-]+@[\w-]+(?:\.[\w-]+)+")]
    private static partial Regex EmailPattern();
    [GeneratedRegex("(\\s[\\w:-]+=\")([^\"]*)\"")]
    private static partial Regex AttributePattern();
}
