using System.Text;
using Campfire.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace Campfire.Features.Chat;

public sealed partial class ChatRenderer
{
    private static readonly object invitationKey = new();
    private sealed record RoomShellKey(long RoomId, string? RoomName, string RoomType, string RoomUpdatedAt,
        long UserId, string UserName, string? UserBio, string UserUpdatedAt, bool Admin,
        string AccountUpdatedAt, bool AccountLogo, string? Styles, string Origin);
    private readonly record struct ShellPiece(ReadOnlyMemory<byte> Bytes, byte Token = 0);
    private sealed record RoomShellPlan(ShellPiece[] Prefix, ShellPiece[] Suffix);

    private bool RequestShowInvitation(long roomId)
    {
        var context = accessor.HttpContext;
        if (context is null) return store.ShowInvitation(roomId);
        if (!context.Items.TryGetValue(invitationKey, out var cached))
            context.Items[invitationKey] = cached = new Dictionary<long, bool>();
        var invitations = (Dictionary<long, bool>)cached!;
        if (!invitations.TryGetValue(roomId, out var value)) invitations[roomId] = value = store.ShowInvitation(roomId);
        return value;
    }

    private IReadOnlyList<HtmlSegment>? CachedRoomShell(RoomRecord room, List<ChatMessage> messages, UserRecord user, string origin)
    {
        var context = accessor.HttpContext;
        // These layouts have additional live dependencies/side effects. Keep
        // their ordinary renderer, including flash consumption and direct names.
        if (context is null || suppressCsrf.Value || room.Direct ||
            !string.IsNullOrWhiteSpace(context.Request.Headers["Turbo-Frame"]) ||
            !string.IsNullOrEmpty(context.Request.Cookies["campfire_chat_alert"])) return null;

        if (!context.Items.ContainsKey(layoutAccountKey) && !context.Items.ContainsKey(invitationKey))
        {
            var current = store.RoomLayoutAccount(room.Id);
            context.Items[layoutAccountKey] = current;
            context.Items[invitationKey] = new Dictionary<long, bool> { [room.Id] = current.ShowInvitation };
        }
        if (RequestShowInvitation(room.Id)) return null;

        // Authorization and these account/invitation reads still happen on every
        // request. Snapshot actual template inputs, never just model timestamps.
        var account = LayoutAccount();
        var key = new RoomShellKey(room.Id, room.Name, room.Type, room.UpdatedAt,
            user.Id, user.Name, user.Bio, user.UpdatedAt, user.IsAdmin,
            account.UpdatedAt, account.HasLogo, account.CustomStyles, origin);
        if (!messageFragments.TryGetValue(key, out RoomShellPlan? plan) || plan is null)
        {
            var marker = "<campfire-room-slot-" + Guid.NewGuid().ToString("N") + "></campfire-room-slot>";
            var html = RoomShell(room, marker, user, origin);
            var position = html.IndexOf(marker, StringComparison.Ordinal);
            if (position < 0) throw new InvalidOperationException("Room message slot is missing.");
            ShellPiece[] Pieces(string value)
            {
                var parts = new List<HtmlSegment>();
                AppendShellSegments(parts, value);
                // Never retain dynamic token owners in a global plan.
                return parts.Select(part => part.Cacheable ? new ShellPiece(part.Bytes)
                    : new ShellPiece(default, part.Bytes.Span.StartsWith("<meta"u8) ? (byte)2 : (byte)1)).ToArray();
            }
            plan = new(Pieces(html[..position]), Pieces(html[(position + marker.Length)..]));
            var keyChars = (long)(room.Name?.Length ?? 0) + room.Type.Length + room.UpdatedAt.Length + user.Name.Length +
                (user.Bio?.Length ?? 0) + user.UpdatedAt.Length + account.UpdatedAt.Length + (account.CustomStyles?.Length ?? 0) + origin.Length;
            var size = 2048 + 2 * keyChars + plan.Prefix.Concat(plan.Suffix).Sum(piece => (long)piece.Bytes.Length + 32);
            messageFragments.Set(key, plan, new MemoryCacheEntryOptions { Size = size });
        }
        var form = RequestCsrfBytes();
        var meta = Encoding.UTF8.GetBytes("<meta name=\"csrf-token\" content=\"" +
            E(context.RequestServices.GetRequiredService<IAuthService>().CsrfToken(context)) + "\">");
        var result = new List<HtmlSegment>(messages.Count * 20 + plan.Prefix.Length + plan.Suffix.Length);
        void Append(ShellPiece[] pieces)
        {
            foreach (var piece in pieces)
                result.Add(piece.Token == 0 ? new(piece.Bytes, Cacheable: true) : new(piece.Token == 1 ? form : meta));
        }
        Append(plan.Prefix);
        result.AddRange(MessagesContentSegments(messages, user.Id));
        Append(plan.Suffix);
        return result;
    }
}
