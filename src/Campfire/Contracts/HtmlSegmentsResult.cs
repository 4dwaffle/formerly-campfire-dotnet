namespace Campfire.Contracts;

/// <summary>Writes already encoded HTML without joining it into a large string.</summary>
public readonly record struct HtmlSegment(ReadOnlyMemory<byte> Bytes, bool Cacheable = false);

public sealed class HtmlSegmentsResult : IResult
{
    private readonly IReadOnlyList<HtmlSegment> segments;
    public HtmlSegmentsResult(IReadOnlyList<HtmlSegment> segments) => this.segments = segments;
    public HtmlSegmentsResult(IReadOnlyList<ReadOnlyMemory<byte>> segments)
        => this.segments = segments.Select(bytes => new HtmlSegment(bytes)).ToArray();
    internal static readonly object CaptureKey = new();
    internal static readonly object SegmentsKey = new();

    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        long length = 0;
        foreach (var segment in segments) length = checked(length + segment.Bytes.Length);
        context.Response.ContentLength = length;
        // The compatibility middleware can hash these immutable segments and
        // write them directly after evaluating conditional/HEAD responses.
        if (context.Items.ContainsKey(CaptureKey))
        {
            context.Items[SegmentsKey] = segments;
            return;
        }
        foreach (var segment in segments)
            if (!segment.Bytes.IsEmpty) await context.Response.Body.WriteAsync(segment.Bytes, context.RequestAborted);
    }
}
