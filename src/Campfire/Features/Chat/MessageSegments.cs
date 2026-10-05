using System.Text;
using Campfire.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace Campfire.Features.Chat;

public sealed partial class ChatRenderer
{
    private static readonly object csrfBytesKey = new();
    private sealed record MessageFragmentKey(long Id, string UpdatedAt, string Origin, string TemplateVersion = "presentation-v3");
    private sealed record ShellFragmentKey(string Html);

    [System.Text.RegularExpressions.GeneratedRegex("<input type=\"hidden\" name=\"authenticity_token\" value=\"[^\"]*\">|<meta name=\"csrf-token\" content=\"[^\"]*\">", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ShellCsrfPattern();

    private sealed class EncodedFragment
    {
        public byte[] Bytes { get; }
        public int[] FormEnds { get; }
        public EncodedFragment(string html)
        {
            Bytes = Encoding.UTF8.GetBytes(html);
            // Regex indices are UTF-16 offsets. Convert successive spans once
            // when encoding the fragment, including multibyte names and emoji.
            var offsets = new List<int>();
            var characterOffset = 0;
            var byteOffset = 0;
            foreach (System.Text.RegularExpressions.Match match in FormPattern().Matches(html))
            {
                var end = match.Index + match.Length;
                byteOffset += Encoding.UTF8.GetByteCount(html.AsSpan(characterOffset, end - characterOffset));
                offsets.Add(byteOffset);
                characterOffset = end;
            }
            FormEnds = offsets.ToArray();
        }
    }

    public IReadOnlyList<ReadOnlyMemory<byte>> RoomSegments(RoomRecord room, List<ChatMessage> messages, UserRecord user, string origin)
        => RoomContentSegments(room, messages, user, origin).Select(segment => segment.Bytes).ToArray();

    public IReadOnlyList<HtmlSegment> RoomContentSegments(RoomRecord room, List<ChatMessage> messages, UserRecord user, string origin)
        => CachedRoomShell(room, messages, user, origin)
            ?? ShellSegments(marker => RoomShell(room, marker, user, origin), messages, user.Id);

    public IReadOnlyList<ReadOnlyMemory<byte>> SearchSegments(UserRecord user, string query, List<ChatMessage> messages, long? returnRoom)
        => SearchContentSegments(user, query, messages, returnRoom).Select(segment => segment.Bytes).ToArray();

    public IReadOnlyList<HtmlSegment> SearchContentSegments(UserRecord user, string query, List<ChatMessage> messages, long? returnRoom)
        => ShellSegments(marker => SearchShell(user, query, messages.Count, returnRoom, marker), messages, user.Id);

    private IReadOnlyList<HtmlSegment> ShellSegments(Func<string, string> shell, List<ChatMessage> messages, long viewerId)
    {
        // The shell contains no message HTML. A fresh marker cannot collide
        // with escaped user fields or any previously stored message content.
        var marker = "<campfire-fragments-" + Guid.NewGuid().ToString("N") + "></campfire-fragments>";
        var html = shell(marker);
        var position = html.IndexOf(marker, StringComparison.Ordinal);
        if (position < 0) throw new InvalidOperationException("Message fragment slot is missing.");
        var segments = new List<HtmlSegment>(messages.Count * 20 + 24);
        AppendShellSegments(segments, html[..position]);
        segments.AddRange(MessagesContentSegments(messages, viewerId));
        AppendShellSegments(segments, html[(position + marker.Length)..]);
        return segments;
    }

    private void AppendShellSegments(List<HtmlSegment> segments, string html)
    {
        // Render the shell from fresh authorization/model/account data first.
        // Intern only exact, token-free output pieces, so no dependency version
        // assumptions or user/permission result caches can make it stale.
        var position = 0;
        foreach (var match in ShellCsrfPattern().EnumerateMatches(html))
        {
            AppendStatic(html[position..match.Index]);
            var field = html.AsSpan(match.Index, match.Length);
            // Reuse the same request-local input owner as message forms, which
            // also lets the gzip plan reference repetitions inside this response.
            var form = accessor.HttpContext?.Items[csrfInputKey] as string;
            segments.Add(new(form is not null && field.SequenceEqual(form)
                ? RequestCsrfBytes() : Encoding.UTF8.GetBytes(field.ToString())));
            position = match.Index + match.Length;
        }
        AppendStatic(html[position..]);

        void AppendStatic(string value)
        {
            if (value.Length == 0) return;
            var key = new ShellFragmentKey(value);
            if (!messageFragments.TryGetValue(key, out byte[]? encoded) || encoded is null)
            {
                encoded = Encoding.UTF8.GetBytes(value);
                // Share the existing bounded presentation budget. The key owns
                // UTF-16 and the value owns UTF-8; account for both copies.
                messageFragments.Set(key, encoded, new MemoryCacheEntryOptions
                {
                    Size = checked(2L * value.Length + encoded.LongLength + 256)
                });
            }
            segments.Add(new(encoded, Cacheable: true));
        }
    }

    public IReadOnlyList<ReadOnlyMemory<byte>> MessagesSegments(List<ChatMessage> messages, long viewerId)
        => MessagesContentSegments(messages, viewerId).Select(segment => segment.Bytes).ToArray();

    public IReadOnlyList<HtmlSegment> MessagesContentSegments(List<ChatMessage> messages, long viewerId)
    {
        if (messages.Count == 0) return Array.Empty<HtmlSegment>();
        var fragments = CachedFragments(messages, viewerId);
        var segments = new List<HtmlSegment>(messages.Count * 20);
        var csrf = RequestCsrfBytes();
        foreach (var fragment in fragments)
        {
            var offset = 0;
            foreach (var end in fragment.FormEnds)
            {
                segments.Add(new(fragment.Bytes.AsMemory(offset, end - offset), Cacheable: true));
                if (!csrf.IsEmpty) segments.Add(new(csrf));
                offset = end;
            }
            segments.Add(new(fragment.Bytes.AsMemory(offset), Cacheable: true));
        }
        return segments;
    }

    private ReadOnlyMemory<byte> RequestCsrfBytes()
    {
        var context = accessor.HttpContext;
        if (suppressCsrf.Value || context is null) return ReadOnlyMemory<byte>.Empty;
        if (context.Items.TryGetValue(csrfBytesKey, out var existing)) return (byte[])existing!;
        var bytes = Encoding.UTF8.GetBytes(CsrfInput());
        context.Items[csrfBytesKey] = bytes;
        return bytes;
    }

    private EncodedFragment[] CachedFragments(List<ChatMessage> messages, long viewerId)
    {
        var origin = Origin();
        var fragments = new EncodedFragment[messages.Count];
        List<long>? missing = null;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            // Pinned Rails caches [message, "presentation-v3"]. A warm
            // fragment intentionally remains unchanged until the message is
            // touched, even when creator/body/attachment dependencies change.
            if (messageFragments.TryGetValue(new MessageFragmentKey(message.Id, message.UpdatedAt, origin), out EncodedFragment? fragment) && fragment is not null)
            {
                fragments[i] = fragment;
                Interlocked.Increment(ref fragmentHits);
            }
            else if (!message.PresentationHydrated) (missing ??= []).Add(message.Id);
        }
        // Recheck membership during hydration; a revoked scope never becomes
        // an unrestricted ID lookup on a cache miss.
        var hydrated = missing is null ? null : store.HydrateMessages(missing, viewerId).ToDictionary(x => x.Id);
        for (var i = 0; i < messages.Count; i++)
        {
            if (fragments[i] is not null) continue;
            var message = messages[i];
            if (!message.PresentationHydrated && (hydrated is null || !hydrated.TryGetValue(message.Id, out message))) throw new ChatHttpException(404, "Message not found");
            Interlocked.Increment(ref fragmentMisses);
            var roomName = MessageRoomName(message);
            var previous = suppressCsrf.Value;
            suppressCsrf.Value = true;
            EncodedFragment fragment;
            try { fragment = new EncodedFragment(RenderMessageHtml(message, roomName)); }
            finally { suppressCsrf.Value = previous; }
            // A concurrent touch between selection and hydration must not
            // poison the earlier model version with newly rendered content.
            var key = new MessageFragmentKey(message.Id, message.UpdatedAt, origin);
            messageFragments.Set(key, fragment, new MemoryCacheEntryOptions
            {
                Size = FragmentSize(key, fragment)
            });
            fragments[i] = fragment;
        }
        return fragments;
    }

    private static long FragmentSize(MessageFragmentKey key, EncodedFragment fragment)
    {
        var characters = (long)key.UpdatedAt.Length + key.Origin.Length + key.TemplateVersion.Length;
        // String rendering, HTTP segments and Cable use the same bounded cache.
        // Body/creator/boost/blob graphs are neither keys nor retained values.
        return checked(2 * characters + fragment.Bytes.LongLength + 4L * fragment.FormEnds.LongLength + 2048);
    }
}
