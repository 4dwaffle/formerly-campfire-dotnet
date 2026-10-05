using System.Buffers;
using System.Security.Cryptography;

namespace Campfire.Features.WebSupport;

// Capture conditional responses without repeatedly growing and copying a large
// contiguous array. Each rented segment belongs exclusively to this response.
internal sealed class PooledResponseBuffer : Stream
{
    private const int SegmentSize = 32 * 1024;
    private readonly List<byte[]> segments = [];
    private long length;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => length;
    public override long Position { get => length; set { if (value != 0) throw new NotSupportedException(); } }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var index = checked((int)(length / SegmentSize));
            var offset = (int)(length % SegmentSize);
            if (index == segments.Count) segments.Add(ArrayPool<byte>.Shared.Rent(SegmentSize));
            var count = Math.Min(SegmentSize - offset, buffer.Length);
            buffer[..count].CopyTo(segments[index].AsSpan(offset, count));
            buffer = buffer[count..];
            length += count;
        }
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }
    public byte[] Sha256()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var index = 0; index < segments.Count; index++)
            hash.AppendData(segments[index].AsSpan(0, checked((int)Math.Min(SegmentSize, length - (long)index * SegmentSize))));
        return hash.GetHashAndReset();
    }
    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        for (var index = 0; index < segments.Count; index++)
            await destination.WriteAsync(segments[index].AsMemory(0, checked((int)Math.Min(SegmentSize, length - (long)index * SegmentSize))), cancellationToken);
    }
    public override void SetLength(long value)
    {
        if (value != 0) throw new NotSupportedException();
        foreach (var segment in segments) ArrayPool<byte>.Shared.Return(segment, clearArray: true);
        segments.Clear();
        length = 0;
    }
    protected override void Dispose(bool disposing) { if (disposing) SetLength(0); base.Dispose(disposing); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
}
