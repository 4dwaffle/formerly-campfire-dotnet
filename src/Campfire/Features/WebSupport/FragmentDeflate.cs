using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using System.IO.Compression;

namespace Campfire.Features.WebSupport;

internal static partial class FragmentDeflate
{
    [LibraryImport("campfire_compression", EntryPoint = "campfire_deflate_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint Create(int level);
    [LibraryImport("campfire_compression", EntryPoint = "campfire_deflate_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void Destroy(nint context);
    [LibraryImport("campfire_compression", EntryPoint = "campfire_crc32")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial uint Checksum(byte* input, uint length);
    [LibraryImport("campfire_compression", EntryPoint = "campfire_deflate_run")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int Run(DeflateContext context, byte* input, uint inputLength, byte* dictionary,
        uint dictionaryLength, byte* output, uint outputCapacity, out uint written, out uint checksum);
    private sealed class DeflateContext : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal DeflateContext(int level) : base(true)
        {
            SetHandle(Create(level));
            if (IsInvalid) throw new OutOfMemoryException("Native deflate context allocation failed.");
        }
        protected override bool ReleaseHandle() { Destroy(handle); return true; }
    }
    private sealed class ContextPool(int level)
    {
        // At most 64 process-lifetime contexts, allocated lazily. Idle handles
        // remain reusable until shutdown; failed leases are destroyed. Their
        // bounded native buffers are separate from the managed fragment budget.
        // A cold burst beyond this bound waits for an exclusive available lease.
        private readonly SemaphoreSlim slots = new(64, 64);
        private readonly ConcurrentQueue<DeflateContext> idle = new();
        internal DeflateContext Rent()
        {
            slots.Wait();
            try { return idle.TryDequeue(out var context) ? context : new(level); }
            catch { slots.Release(); throw; }
        }
        internal void Return(DeflateContext context, bool healthy)
        {
            if (healthy) idle.Enqueue(context); else context.Dispose();
            slots.Release();
        }
    }
    private static readonly ContextPool cached = new(6);

    internal static unsafe FragmentPiece Encode(ReadOnlyMemory<byte> input, ReadOnlySpan<byte> dictionary, int level)
    {
        if (level == 1)
        {
            // Only dynamic input uses this path. Flush produces a non-final raw
            // sync-flush boundary; capture it before Dispose writes the final
            // block. It needs no dictionary, and no token bytes are retained.
            // The .NET runtime's optimized backend is substantially faster here
            // than the platform zlib used for cold immutable pieces.
            using var outputStream = new MemoryStream();
            using var deflater = new DeflateStream(outputStream,CompressionLevel.Fastest,true);
            deflater.Write(input.Span); deflater.Flush();
            fixed (byte* inputPointer = input.Span)
                return new(outputStream.ToArray(), Checksum(inputPointer,(uint)input.Length),FragmentCrc.Shift(input.Length));
        }
        // More generous than zlib deflateBound for the fixed default window/memory
        // settings, including an extra stored flush block.
        var capacity = checked(input.Length + input.Length / 8 + 128);
        var pool = cached;
        var context = pool.Rent();
        byte[]? output = null;
        var healthy = false;
        try
        {
            output = ArrayPool<byte>.Shared.Rent(capacity);
            fixed (byte* inputPointer = input.Span)
            fixed (byte* dictionaryPointer = dictionary)
            fixed (byte* outputPointer = output)
            {
                var result = Run(context, inputPointer, (uint)input.Length, dictionaryPointer, (uint)dictionary.Length,
                    outputPointer, (uint)capacity, out var written, out var crc);
                if (result != 0) throw new InvalidOperationException($"Fragment deflate failed ({result}).");
                healthy = true;
                return new(output.AsSpan(0, checked((int)written)).ToArray(), crc, FragmentCrc.Shift(input.Length));
            }
        }
        finally
        {
            pool.Return(context, healthy);
            if (output is not null) ArrayPool<byte>.Shared.Return(output, clearArray: true);
        }
    }
}

internal readonly record struct FragmentPiece(byte[] Deflate, uint Crc, uint Shift);

internal static class FragmentCrc
{
    // The reflected CRC polynomial combination used by zlib crc32_combine.
    // Precompute powers once; each cached fragment retains its length operator.
    private static readonly uint[] powers = Powers();
    private static uint[] Powers()
    {
        var table = new uint[32];
        uint power = 1u << 30;
        for (var i = 0; i < table.Length; i++) { table[i] = power; power = Multiply(power, power); }
        return table;
    }

    internal static uint Shift(int length)
    {
        uint value = 1u << 31;
        var exponent = (uint)length;
        var index = 3;
        while (exponent != 0)
        {
            if ((exponent & 1) != 0) value = Multiply(powers[index % 32], value);
            exponent >>= 1;
            index++;
        }
        return value;
    }

    internal static uint Combine(uint left, FragmentPiece right) => Multiply(left, right.Shift) ^ right.Crc;
    internal static uint Multiply(uint left, uint right)
    {
        uint product = 0;
        for (var bit = 31; bit >= 0; bit--)
        {
            // Fixed-width polynomial multiplication with masks avoids two
            // unpredictable data-dependent branches per CRC bit.
            product ^= right & unchecked(0u - ((left >> bit) & 1));
            right = (right >> 1) ^ (0xedb88320u & unchecked(0u - (right & 1)));
        }
        return product;
    }
}
