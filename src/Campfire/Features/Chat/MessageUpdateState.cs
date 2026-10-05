namespace Campfire.Features.Chat;

internal sealed class MessageUpdateState
{
    public long CreatorId { get; set; }
    public string? ClientMessageId { get; set; }
    public long? BodyId { get; set; }
    public string? Body { get; set; }
    public long? BlobId { get; set; }
}
