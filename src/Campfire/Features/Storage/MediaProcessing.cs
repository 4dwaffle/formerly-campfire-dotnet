using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.Contracts;
using Campfire.Features.Integrations;
using Dapper;

namespace Campfire.Features.Storage;

public sealed partial class MediaService
{
    private readonly Lazy<bool> popplerAvailable=new(()=>PreviewToolExists(configuration["CAMPFIRE_PDFTOPPM_COMMAND"]??"pdftoppm",["-v"],0));
    private readonly Lazy<bool> mupdfAvailable=new(()=>PreviewToolExists(configuration["CAMPFIRE_MUTOOL_COMMAND"]??"mutool",[],1));
    private bool PdfPreviewable=>popplerAvailable.Value||mupdfAvailable.Value;
    private static bool PreviewToolExists(string command,string[] arguments,int expected)
    {
        try
        {
            var start=new ProcessStartInfo(command){RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
            foreach(var argument in arguments)start.ArgumentList.Add(argument);
            using var process=System.Diagnostics.Process.Start(start)!;
            var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(5000)){process.Kill(true);return false;}
            Task.WhenAll(output,error).GetAwaiter().GetResult();return process.ExitCode==expected;
        }
        catch(System.ComponentModel.Win32Exception){return false;}
    }
    public async Task RunJobAsync(string name,JsonElement[] arguments,CancellationToken cancellationToken)
    {
        if(name=="ActiveStorage::MirrorJob")return; // Local disk is not a MirrorService.
        var id=Campfire.Features.Integrations.RailsJobQueue.RecordId(arguments[0]);
        if(name=="ActiveStorage::PurgeJob"){PurgeBlob(id);return;}
        var blob=db.Read(c=>c.QuerySingleOrDefault<BlobRecord>($"SELECT {Columns} FROM active_storage_blobs b WHERE b.id=@id",new{id}));
        if(blob is null)return; // Rails discard_on RecordNotFound.
        switch(name)
        {
            case "ActiveStorage::AnalyzeJob": await AnalyzeAsync(blob,cancellationToken);break;
            case "ActiveStorage::TransformJob":
                var values=JsonSerializer.Deserialize(arguments[1].GetRawText(),StorageJson.Default.DictionaryStringJsonElement)!;
                values.Remove("_aj_symbol_keys");
                var transformSymbolFormat=false;
                if(values.TryGetValue("format",out var format)&&format.ValueKind==JsonValueKind.Object&&format.TryGetProperty("value",out var symbol)){values["format"]=symbol.Clone();transformSymbolFormat=true;}
                await Transform(blob,JsonSerializer.SerializeToElement(values,StorageJson.Default.DictionaryStringJsonElement),cancellationToken,transformSymbolFormat);break;
            case "ActiveStorage::PreviewImageJob":
                var preview=await PreviewAsync(blob,cancellationToken);
                if(preview is not null&&arguments.Length>1)foreach(var variation in arguments[1].EnumerateArray())
                {
                    var jobs=services.GetService<IBackgroundJobs>();
                    if(jobs is not null)
                    {
                        await jobs.EnqueueAsync("ActiveStorage::TransformJob",[RailsJobQueue.GlobalId("ActiveStorage::Blob",blob.Id),JsonNode.Parse(variation.GetRawText())],cancellationToken);
                        continue;
                    }
                    var options=JsonSerializer.Deserialize(variation.GetRawText(),StorageJson.Default.DictionaryStringJsonElement)!;options.Remove("_aj_symbol_keys");var symbolFormat=false;
                    if(options.TryGetValue("format",out var previewFormat)&&previewFormat.ValueKind==JsonValueKind.Object&&previewFormat.TryGetProperty("value",out var previewSymbol)){options["format"]=previewSymbol.Clone();symbolFormat=true;}
                    await Transform(preview,JsonSerializer.SerializeToElement(options,StorageJson.Default.DictionaryStringJsonElement),cancellationToken,symbolFormat);
                }
                break;
            case "ActiveStorage::MirrorJob": break; // Configured local service has no mirrors.
        }
    }
    public async Task ProcessMessageAttachmentAsync(long messageId, CancellationToken cancellationToken = default)
    {
        var blob = Attached("Message",messageId,"attachment");
        if(blob is not null) await ProcessAttachmentAsync(blob,cancellationToken);
    }
    private async Task ProcessAttachmentAsync(BlobRecord blob,CancellationToken cancellationToken)
    {
        await AnalyzeAsync(blob,cancellationToken);
        if(VariableImage(blob)) await Transform(blob,JsonSerializer.SerializeToElement(new StorageVariation(DefaultFormat(blob),[1200,800]),StorageJson.Default.StorageVariation),cancellationToken);
        else if(blob.ContentType?.StartsWith("video/",StringComparison.Ordinal)==true) { using var videoVariation=JsonDocument.Parse("{\"format\":\"webp\"}");await Transform(blob,videoVariation.RootElement,cancellationToken,true); }
        else if(blob.ContentType=="application/pdf"&&PdfPreviewable) await Transform(blob,JsonSerializer.SerializeToElement(new StorageVariation("png",[1200,800]),StorageJson.Default.StorageVariation),cancellationToken);
    }
    private Task<string?> Identify(string path,string filename,string? declared,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();return Task.FromResult<string?>(MarcelMime.Identify(path,filename,declared));
    }
    public async Task AnalyzeAsync(BlobRecord blob, CancellationToken cancellationToken)
    {
        var path = DiskPath(blob.Key);
        if (!File.Exists(path))throw new FileNotFoundException("Active Storage blob bytes were not found",path);
        var metadata = JsonSerializer.Deserialize(blob.Metadata??"{}", StorageJson.Default.DictionaryStringJsonElement) ?? new();
        if(!metadata.TryGetValue("identified",out var identified)||identified.ValueKind!=JsonValueKind.True) blob.ContentType=await Identify(path,blob.Filename,blob.ContentType,cancellationToken);
        if (metadata.TryGetValue("analyzed", out var analyzed) && analyzed.ValueKind == JsonValueKind.True) return;
        if(!await Integrity(blob,cancellationToken))throw new InvalidDataException("Active Storage integrity check failed");
        var successful = true;
        if (VariableImage(blob))
        {
            var width = await Output("vipsheader", ["-f", "width", path], cancellationToken);
            var height = await Output("vipsheader", ["-f", "height", path], cancellationToken);
            if (int.TryParse(width?.Trim(), out var w) && int.TryParse(height?.Trim(), out var h))
            { var orientation=await Output("vipsheader",["-f","orientation",path],cancellationToken);if(int.TryParse(orientation?.Trim(),out var rotation)&&rotation is >=5 and <=8)(w,h)=(h,w);metadata["width"] = JsonSerializer.SerializeToElement(w, StorageJson.Default.Int32); metadata["height"] = JsonSerializer.SerializeToElement(h, StorageJson.Default.Int32); }
            else successful = false;
        }
        else if (blob.ContentType?.StartsWith("video/", StringComparison.Ordinal) == true || blob.ContentType?.StartsWith("audio/", StringComparison.Ordinal) == true)
        {
            var data = await Output("ffprobe", ["-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", path], cancellationToken);
            if (data is not null)
            {
                try
                {
                    using var parsed = JsonDocument.Parse(data);
                    var streams = parsed.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                    var video = streams.FirstOrDefault(x => x.GetProperty("codec_type").GetString() == "video");
                    var isAudio=blob.ContentType?.StartsWith("audio/",StringComparison.Ordinal)==true;
                    if (!isAudio&&video.ValueKind == JsonValueKind.Object)
                    {
                        if (video.TryGetProperty("width", out var w)) metadata["width"] = w.Clone();
                        if (video.TryGetProperty("height", out var h)) metadata["height"] = h.Clone();
                        metadata["video"] = JsonSerializer.SerializeToElement(true, StorageJson.Default.Boolean);
                        int? angle=null;
                        if(video.TryGetProperty("tags",out var tags)&&tags.TryGetProperty("rotate",out var rotation)&&int.TryParse(rotation.GetString(),out var tagAngle))angle=tagAngle;
                        else if(video.TryGetProperty("side_data_list",out var side))foreach(var item in side.EnumerateArray())if(item.TryGetProperty("side_data_type",out var sideType)&&sideType.GetString()=="Display Matrix"&&item.TryGetProperty("rotation",out var rotationValue)&&rotationValue.TryGetInt32(out var rotationAngle))angle=rotationAngle;
                        if(angle is int knownAngle)metadata["angle"]=JsonSerializer.SerializeToElement(knownAngle,StorageJson.Default.Int32);
                        if(angle is 90 or -90 or 270 or -270 && metadata.TryGetValue("width",out var originalWidth)&&metadata.TryGetValue("height",out var originalHeight)) { metadata["width"]=originalHeight;metadata["height"]=originalWidth; }
                        if(video.TryGetProperty("display_aspect_ratio",out var ratio)){var parts=(ratio.GetString()??"").Split(':');if(parts.Length==2&&int.TryParse(parts[0],out var numerator)&&int.TryParse(parts[1],out var denominator)&&numerator!=0){metadata["display_aspect_ratio"]=JsonSerializer.SerializeToElement(new int[]{numerator,denominator},StorageJson.Default.Int32Array);if(video.TryGetProperty("width",out var encodedWidth))metadata[angle is 90 or -90 or 270 or -270?"width":"height"]=JsonSerializer.SerializeToElement(encodedWidth.GetDouble()*denominator/numerator,StorageJson.Default.Double);}}
                    }
                    else if(!isAudio)metadata["video"]=JsonSerializer.SerializeToElement(false,StorageJson.Default.Boolean);
                    if(isAudio)
                    {
                        var audio=streams.FirstOrDefault(x=>x.TryGetProperty("codec_type",out var type)&&type.GetString()=="audio");
                        if(audio.ValueKind==JsonValueKind.Object) foreach(var field in new[]{"bit_rate","sample_rate","tags"}) if(audio.TryGetProperty(field,out var value)) metadata[field]=field=="tags"?value.Clone():JsonSerializer.SerializeToElement(long.Parse(value.GetString()!,System.Globalization.CultureInfo.InvariantCulture),StorageJson.Default.Int64);
                        if(audio.ValueKind==JsonValueKind.Object&&audio.TryGetProperty("duration",out var audioDuration)&&double.TryParse(audioDuration.GetString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var audioSeconds))metadata["duration"]=JsonSerializer.SerializeToElement(audioSeconds,StorageJson.Default.Double);
                    }
                    if(!isAudio){metadata["audio"] = JsonSerializer.SerializeToElement(streams.Any(x => x.TryGetProperty("codec_type", out var type) && type.GetString() == "audio"), StorageJson.Default.Boolean);var duration=video.ValueKind==JsonValueKind.Object&&video.TryGetProperty("duration",out var streamDuration)?streamDuration:parsed.RootElement.TryGetProperty("format",out var format)&&format.TryGetProperty("duration",out var containerDuration)?containerDuration:default;if(duration.ValueKind==JsonValueKind.String&&double.TryParse(duration.GetString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var seconds))metadata["duration"]=JsonSerializer.SerializeToElement(seconds,StorageJson.Default.Double);}
                }
                catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { successful = false; }
            }
            else successful = false;
        }
        if (!successful) return; // Do not label missing analysis as completed.
        metadata["analyzed"] = JsonSerializer.SerializeToElement(true, StorageJson.Default.Boolean);
        metadata["identified"] = JsonSerializer.SerializeToElement(true, StorageJson.Default.Boolean);
        blob.Metadata = JsonSerializer.Serialize(metadata, StorageJson.Default.DictionaryStringJsonElement);
        db.Write((connection, transaction) =>
        {
            connection.Execute("UPDATE active_storage_blobs SET metadata=@Metadata,content_type=@ContentType WHERE id=@Id", blob, transaction);
            foreach (var (type, table) in new[] { ("Message", "messages"), ("User", "users"), ("Account", "accounts") })
                connection.Execute($"UPDATE {table} SET updated_at=@now WHERE id IN (SELECT record_id FROM active_storage_attachments WHERE blob_id=@id AND record_type=@type)", new { now = Campfire.Contracts.RequestUser.Timestamp(), id = blob.Id, type }, transaction);
            connection.Execute("UPDATE rooms SET updated_at=@now WHERE id IN (SELECT room_id FROM messages WHERE id IN (SELECT record_id FROM active_storage_attachments WHERE blob_id=@id AND record_type='Message'))", new { now = Campfire.Contracts.RequestUser.Timestamp(), id = blob.Id }, transaction);
            return 0;
        });
    }
    private async Task<BlobRecord?> PreviewAsync(BlobRecord blob, CancellationToken cancellationToken)
    {
        if(blob.ContentType=="application/pdf"&&!PdfPreviewable)return null;
        await using var previewLock=await VariantLock(blob.Id,"preview_image",cancellationToken);
        try
        {
            var existing = Attached("ActiveStorage::Blob", blob.Id, "preview_image");
            if (existing is not null && File.Exists(DiskPath(existing.Key))) return existing;
            var source = DiskPath(blob.Key);
            if (!File.Exists(source) || !await Integrity(blob, cancellationToken)) return null;
            var image = new BlobRecord { Key = Key(), Filename = Path.ChangeExtension(blob.Filename, "jpg"), ContentType = "image/jpeg", CreatedAt = Campfire.Contracts.RequestUser.Timestamp() };
            var path = DiskPath(image.Key); var temporary = path + ".jpg"; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                if(blob.ContentType=="application/pdf")
                {
                    var prefix=temporary+"-pdf";
                    try
                    {
                        var rendered=popplerAvailable.Value
                            ?await Process(configuration["CAMPFIRE_PDFTOPPM_COMMAND"]??"pdftoppm",["-singlefile","-cropbox","-r","72","-png",source,prefix],cancellationToken)
                            :await Process(configuration["CAMPFIRE_MUTOOL_COMMAND"]??"mutool",["draw","-F","png","-o",prefix+".png",source,"1"],cancellationToken);
                        if(!rendered)return null;
                        File.Move(prefix+".png",temporary,true);image.ContentType="image/png";image.Filename=Path.ChangeExtension(blob.Filename,"png");
                    }
                    finally{File.Delete(prefix+".png");}
                }
                else if (!await Process("ffmpeg", ["-y", "-i", source, "-vf", @"select=eq(n\,0)+eq(key\,1)+gt(scene\,0.015),loop=loop=-1:size=2,trim=start_frame=1", "-frames:v", "1", "-f", "image2", temporary], cancellationToken)) return null;
                File.Move(temporary, path, true); image.ByteSize = new FileInfo(path).Length;
                await using (var stream = File.OpenRead(path)) image.Checksum = Convert.ToBase64String(await MD5.HashDataAsync(stream, cancellationToken));
                db.Write((connection, transaction) =>
                {
                    image.Id = connection.ExecuteScalar<long>("INSERT INTO active_storage_blobs(key,filename,content_type,metadata,service_name,byte_size,checksum,created_at) VALUES (@Key,@Filename,@ContentType,@Metadata,@ServiceName,@ByteSize,@Checksum,@CreatedAt);SELECT last_insert_rowid()", image, transaction);
                    connection.Execute("INSERT INTO active_storage_attachments(blob_id,record_type,record_id,name,created_at) VALUES(@id,'ActiveStorage::Blob',@source,'preview_image',@now)", new { id = image.Id, source = blob.Id, now = image.CreatedAt }, transaction);
                    return 0;
                });
                await AnalyzeAsync(image, cancellationToken);
                return image;
            }
            finally { File.Delete(temporary); }
        }
        finally { }
    }
    private async Task<bool> Integrity(BlobRecord blob, CancellationToken cancellationToken)
    {
        if (blob.Checksum is null) return true;
        await using var stream = File.OpenRead(DiskPath(blob.Key));
        try { return CryptographicOperations.FixedTimeEquals(await MD5.HashDataAsync(stream, cancellationToken), Convert.FromBase64String(blob.Checksum)); }
        catch (FormatException) { return false; }
    }
    private async Task<string?> Output(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            if(executable=="vipsheader" && configuration["CAMPFIRE_VIPS_COMMAND"] is string helper)
            {
                var original=arguments.ToArray();arguments=["header",original[1],original[2]];executable=helper;
            }
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.Environment["VIPS_BLOCK_UNTRUSTED"]="1";
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(true); return null; }
            await errors;
            return process.ExitCode == 0 ? await output : null;
        }
        catch (System.ComponentModel.Win32Exception) { logger.LogDebug("Media analyzer {Executable} unavailable", executable); return null; }
    }
}
