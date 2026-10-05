namespace Campfire.Features.Chat;

public sealed class RoomRecord
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string Type { get; set; } = "Rooms::Open";
    public long CreatorId { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string Involvement { get; set; } = "mentions";
    public string? UnreadAt { get; set; }
    public bool Direct => Type == "Rooms::Direct";
    public string Dom(string prefix) => $"{prefix}_{(Direct ? "rooms_direct" : Type == "Rooms::Closed" ? "rooms_closed" : "rooms_open")}_{Id}";
    public string EditPath => $"/rooms/{(Direct ? "directs" : Type == "Rooms::Closed" ? "closeds" : "opens")}/{Id}/edit";
}

public sealed class ChatMessage
{
    public long Id { get; set; }
    public long RoomId { get; set; }
    public long CreatorId { get; set; }
    public string ClientMessageId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string Body { get; set; } = "";
    public string CreatorName { get; set; } = "";
    public string? CreatorBio { get; set; }
    public string CreatorUpdatedAt { get; set; } = "";
    public int CreatorRole { get; set; }
    public bool CreatorMissing { get; set; }
    public string? RoomName { get; set; }
    public string RoomType { get; set; } = "";
    public string? AttachmentFilename { get; set; }
    public string? AttachmentMetadata { get; set; }
    public long? AttachmentBlobId { get; set; }
    public List<ChatBoost> Boosts { get; set; } = [];
    // HTML pages can carry authorized model/dependency versions without loading
    // presentation bodies or boost objects. Editing and JSON use hydrated rows.
    public bool PresentationHydrated { get; set; } = true;
}

public sealed class ChatBoost
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public long BoosterId { get; set; }
    public string Content { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string BoosterName { get; set; } = "";
    public string? BoosterBio { get; set; }
    public string BoosterUpdatedAt { get; set; } = "";
}

public sealed class AccountPresentation
{
    public long Id { get; set; }
    public string Name { get; set; } = "Campfire";
    public string JoinCode { get; set; } = "";
    public string? CustomStyles { get; set; }
    public string? Settings { get; set; }
    public string UpdatedAt { get; set; } = "";
    public bool HasLogo { get; set; }
    public bool ShowInvitation { get; set; }
}

internal sealed class SidebarMember
{
    public long RoomId { get; set; }
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Bio { get; set; }
    public string UpdatedAt { get; set; } = "";
}

public sealed class ChatHttpException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
