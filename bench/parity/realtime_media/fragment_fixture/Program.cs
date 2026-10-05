using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Campfire.Contracts;
using Campfire.Features.WebSupport;

if (args.Length != 3 && args.Length != 5) throw new ArgumentException("capture-path encoded-output-path report-path [micro-repetitions micro-report-path]");
var body = File.ReadAllBytes(args[0]);
// Latin1 preserves byte offsets while inspecting ASCII HTML delimiters. All
// output segments retain original UTF8 bytes, including multibyte content.
var html = Encoding.Latin1.GetString(body);
var roots = new List<(int Start, int End)>();
var depth = 0;
(int Start, int Depth)? root = null;
foreach (Match tag in Regex.Matches(html, @"</?div\b[^>]*>"))
{
    if (tag.Value.StartsWith("</", StringComparison.Ordinal))
    {
        depth--;
        if (root is { } active && depth == active.Depth) { roots.Add((active.Start, tag.Index + tag.Length)); root = null; }
    }
    else
    {
        if (Regex.IsMatch(tag.Value, "\\bid=\"message_[^$][^\"]*\"")) root = (tag.Index, depth);
        depth++;
    }
}
if (roots.Count == 0) throw new InvalidOperationException("Capture contains no real messages.");
var segments = new List<HtmlSegment>();
var requestTokens = new Dictionary<string, byte[]>(StringComparer.Ordinal);
var position = 0;
foreach (var (start, end) in roots)
{
    if (start > position) segments.Add(new(body.AsMemory(position, start - position)));
    var message = html.Substring(start, end - start);
    var tokens = Regex.Matches(message, "<input[^>]*name=\"authenticity_token\"[^>]*>");
    using var stable = new MemoryStream();
    var slots = new List<(int Offset, int Length, byte[] Token)>();
    var offset = 0;
    foreach (Match token in tokens)
    {
        var length = token.Index - offset;
        var stableOffset = checked((int)stable.Length);
        stable.Write(body.AsSpan(start + offset, length));
        if (!requestTokens.TryGetValue(token.Value, out var tokenBytes))
            requestTokens.Add(token.Value, tokenBytes = body.AsSpan(start + token.Index, token.Length).ToArray());
        slots.Add((stableOffset, length, tokenBytes));
        offset = token.Index + token.Length;
    }
    stable.Write(body.AsSpan(start + offset, end - start - offset));
    var owner = stable.ToArray(); // Entire immutable owner excludes tokens.
    var last = 0;
    foreach (var slot in slots)
    {
        segments.Add(new(owner.AsMemory(slot.Offset, slot.Length), true));
        segments.Add(new(slot.Token));
        last = slot.Offset + slot.Length;
    }
    segments.Add(new(owner.AsMemory(last), true));
    position = end;
}
if (position < body.Length) segments.Add(new(body.AsMemory(position)));
if (!body.AsSpan().SequenceEqual(segments.SelectMany(s => s.Bytes.ToArray()).ToArray())) throw new InvalidOperationException("Segmentation changed bytes.");
var encoder = new FragmentGzipEncoder();
if (!encoder.TryEncode(segments, out var cold) || !encoder.NativeAvailable) throw new InvalidOperationException("Native encoder unavailable.");
var coldEntries = encoder.CachedEntries;
if (!encoder.TryEncode(segments, out var warm) || coldEntries != encoder.CachedEntries) throw new InvalidOperationException("Warm cache did not reuse descriptors.");
foreach (var encoded in new[] { cold, warm })
{
    using var decoder = new GZipStream(new MemoryStream(encoded), CompressionMode.Decompress);
    using var decoded = new MemoryStream();
    decoder.CopyTo(decoded);
    if (!body.AsSpan().SequenceEqual(decoded.ToArray())) throw new InvalidOperationException("Independent .NET gzip decode mismatch.");
}
using var whole = new MemoryStream();
using (var compressor = new GZipStream(whole, CompressionLevel.Fastest, true)) compressor.Write(body);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllBytes(args[1], warm);
var report = new JsonObject
{
    ["capture"] = args[0], ["capture_sha256"] = Convert.ToHexStringLower(SHA256.HashData(body)),
    ["decoded_bytes"] = body.Length, ["messages"] = roots.Count, ["segments"] = segments.Count,
    ["whole_gzip_fastest_bytes"] = whole.Length, ["native_fragment_bytes"] = warm.Length,
    ["native_fragment_sha256"] = Convert.ToHexStringLower(SHA256.HashData(warm)),
    ["cold_warm_bytes_equal"] = cold.AsSpan().SequenceEqual(warm), ["cache_entries"] = encoder.CachedEntries,
    ["assembly_plan_entries"] = encoder.CachedPlanEntries,
    ["cache_reserved_bytes"] = encoder.CachedBytes, ["native_backend"] = encoder.NativeAvailable,
    ["exact_dotnet_gzip_decode"] = true, ["timing_benchmark"] = false
};
File.WriteAllText(args[2], report.ToJsonString(new() { WriteIndented = true }) + "\n");
Console.WriteLine(report.ToJsonString());

if (args.Length == 5)
{
    var repetitions = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
    var cases = Enumerable.Range(0, 32).Select(index => FreshSegments(segments,index)).ToArray();
    byte[] Native(IReadOnlyList<HtmlSegment> value)
    {
        if (!encoder.TryEncode(value,out var encoded) || !encoder.NativeAvailable) throw new InvalidOperationException("Native path unavailable.");
        return encoded;
    }
    foreach (var input in cases) { _ = Native(input); _ = Managed(input); }
    var warmup = Stopwatch.StartNew();
    var warmupIterations = 0;
    do
    {
        var input = cases[warmupIterations % cases.Length];
        _ = Native(input); _ = Managed(input);
        warmupIterations++;
    } while (warmup.Elapsed < TimeSpan.FromSeconds(2) || warmupIterations < 3000);
    warmup.Stop();
    var rounds = new JsonArray();
    for (var round = 0; round < 5; round++)
    {
        foreach (var native in round % 2 == 0 ? new[] { true,false } : new[] { false,true })
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            long encodedBytes = 0;
            for (var index = 0; index < repetitions; index++)
                encodedBytes += (native ? Native(cases[index % cases.Length]) : Managed(cases[index % cases.Length])).Length;
            var elapsed = Stopwatch.GetElapsedTime(start);
            rounds.Add(new JsonObject
            {
                ["round"] = round,["backend"] = native ? "native-fragments" : "managed-whole-coalesced32k",
                ["repetitions"] = repetitions,["elapsed_ms"] = elapsed.TotalMilliseconds,
                ["allocated_bytes"] = GC.GetAllocatedBytesForCurrentThread() - allocated,["encoded_bytes_sum"] = encodedBytes
            });
        }
    }
    var codecFiles = new[] { "FragmentGzipEncoder.cs","FragmentDeflate.cs","FragmentHistory.cs","FragmentBackreference.cs","FragmentCompressionPlan.cs","native/fragment-deflate.c" };
    var sources = new JsonObject();
    foreach (var file in codecFiles) sources[file] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes("src/Campfire/Features/WebSupport/"+file)));
    var micro = new JsonObject
    {
        ["kind"] = "warm in-memory encode-only micro; no HTTP throughput claim",
        ["warmup_iterations_per_backend"] = warmupIterations,["warmup_elapsed_ms"] = warmup.Elapsed.TotalMilliseconds,
        ["capture_sha256"] = Convert.ToHexStringLower(SHA256.HashData(body)),["decoded_bytes"] = body.Length,
        ["segments"] = segments.Count,["fresh_dynamic_variants"] = cases.Length,["rounds"] = rounds,["codec_file_sha256"] = sources
    };
    File.WriteAllText(args[4],micro.ToJsonString(new() { WriteIndented = true })+"\n");
    Console.WriteLine(micro.ToJsonString());
}

static HtmlSegment[] FreshSegments(IReadOnlyList<HtmlSegment> source,int variation)
{
    var local = new Dictionary<ArraySegment<byte>,byte[]>();
    var result = new HtmlSegment[source.Count];
    for (var index = 0; index < source.Count; index++)
    {
        var segment = source[index];
        if (segment.Cacheable) { result[index] = segment; continue; }
        MemoryMarshal.TryGetArray(segment.Bytes,out var slice);
        if (!local.TryGetValue(slice,out var bytes))
        {
            bytes = segment.Bytes.ToArray();
            var position = bytes.AsSpan().IndexOf("value=\""u8);
            if (position >= 0 && position + 7 < bytes.Length) bytes[position + 7] = (byte)('A'+variation%26);
            local.Add(slice,bytes);
        }
        result[index] = new(bytes);
    }
    return result;
}

static byte[] Managed(IReadOnlyList<HtmlSegment> segments)
{
    using var output = new MemoryStream();
    using (var compressor = new GZipStream(output,CompressionLevel.Fastest,true))
    {
        const int capacity = 32768;
        var buffer = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            var used = 0;
            foreach (var segment in segments)
            {
                var remaining = segment.Bytes.Span;
                while (!remaining.IsEmpty)
                {
                    var count = Math.Min(capacity-used,remaining.Length);
                    remaining[..count].CopyTo(buffer.AsSpan(used)); remaining = remaining[count..]; used += count;
                    if (used == capacity) { compressor.Write(buffer.AsSpan(0,used)); used = 0; }
                }
            }
            if (used > 0) compressor.Write(buffer.AsSpan(0,used));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer,true); }
    }
    return output.ToArray();
}
