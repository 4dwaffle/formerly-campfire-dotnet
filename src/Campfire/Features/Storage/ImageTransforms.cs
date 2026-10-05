using System.Globalization;
using System.Text.Json;

namespace Campfire.Features.Storage;

public sealed partial class MediaService
{
    private async Task<bool> TransformImage(string source,string destination,JsonElement transformations,CancellationToken cancellationToken)
    {
        var current=source;var intermediate=new List<string>();
        static string Number(JsonElement value)=>value.ValueKind==JsonValueKind.String?value.GetString()!:value.ValueKind==JsonValueKind.Array?string.Join(' ',value.EnumerateArray().Select(Number)):value.GetRawText();
        static IEnumerable<string> Options(JsonElement value,params string[] ignored)
        {
            if(value.ValueKind!=JsonValueKind.Object)yield break;
            foreach(var option in value.EnumerateObject())if(!ignored.Contains(option.Name)){if(!System.Text.RegularExpressions.Regex.IsMatch(option.Name,"^[A-Za-z0-9_]+$"))throw new ArgumentException("Invalid operation option");yield return "--"+option.Name.Replace('_','-');yield return Number(option.Value);}
        }
        try
        {
            var operations=transformations.EnumerateObject().Where(p=>p.Name is not ("format" or "saver" or "loader")).ToArray();
            if(transformations.TryGetProperty("loader",out var loadOptions))
            {
                var loaded=destination+"-load.tiff";intermediate.Add(loaded);
                var sourceOptions=loadOptions.EnumerateObject().Where(p=>p.Name is not ("autorot" or "loader")).ToArray();
                var command=loadOptions.TryGetProperty("loader",out var loader)?new List<string>{loader.GetString()+"load",source,loaded}:new List<string>{"copy",source+(sourceOptions.Length==0?"":"["+string.Join(',',sourceOptions.Select(p=>p.Name+"="+Number(p.Value)))+"]"),loaded};
                if(loader.ValueKind!=JsonValueKind.Undefined)command.AddRange(Options(loadOptions,"loader","autorot"));
                if(!await Process("vips",command,cancellationToken))return false;current=loaded;
                if(!loadOptions.TryGetProperty("autorotate",out _)&&(!loadOptions.TryGetProperty("autorot",out var autorot)||autorot.ValueKind!=JsonValueKind.False)){var upright=destination+"-upright.tiff";intermediate.Add(upright);if(!await Process("vips",["autorot",current,upright],cancellationToken))return false;current=upright;}
            }
            else if(operations.Length==0||!operations[0].Name.StartsWith("resize_to_",StringComparison.Ordinal)&&operations[0].Name!="resize_and_pad")
            {var upright=destination+"-upright.tiff";intermediate.Add(upright);if(!await Process("vips",["autorot",current,upright],cancellationToken))return false;current=upright;}
            foreach(var operation in operations)
            {
                if(operation.Name is "format" or "saver" or "loader")continue;
                var output=destination+"-"+intermediate.Count+".tiff";intermediate.Add(output);
                var value=operation.Value;
                var args=value.ValueKind==JsonValueKind.Array?value.EnumerateArray().ToArray():[value];
                var operationOptions=args.Length>0&&args[^1].ValueKind==JsonValueKind.Object?args[^1]:default;
                string[] command;
                switch(operation.Name)
                {
                    case "resize_to_limit":case "resize_to_fit":case "resize_to_fill":case "resize_to_cover":case "resize_and_pad":
                        var width=args[0].ValueKind==JsonValueKind.Null?0:args[0].GetInt32();var height=args[1].ValueKind==JsonValueKind.Null?0:args[1].GetInt32();
                        if(width<=0&&height<=0)throw new ArgumentException("Invalid resize dimensions");
                        if(operation.Name=="resize_to_cover"){var originalWidth=int.Parse((await Output("vipsheader",["-f","width",current],cancellationToken))!.Trim(),CultureInfo.InvariantCulture);var originalHeight=int.Parse((await Output("vipsheader",["-f","height",current],cancellationToken))!.Trim(),CultureInfo.InvariantCulture);if((double)originalWidth/originalHeight>(double)width/height)width=10000000;else height=10000000;}
                        var resize=new List<string>{"thumbnail",current,output,(width>0?width:10000000).ToString(CultureInfo.InvariantCulture)};
                        if(operationOptions.ValueKind!=JsonValueKind.Object||!operationOptions.TryGetProperty("size",out _))resize.AddRange(["--size",operation.Name=="resize_to_limit"?"down":"both"]);
                        if(height>0)resize.AddRange(["--height",height.ToString(CultureInfo.InvariantCulture)]);
                        if(operation.Name=="resize_to_fill"&&(operationOptions.ValueKind!=JsonValueKind.Object||!operationOptions.TryGetProperty("crop",out _)))resize.AddRange(["--crop","centre"]);
                        resize.AddRange(Options(operationOptions,"sharpen","gravity","background","extend","alpha"));
                        command=resize.ToArray();break;
                    case "resize":command=["resize",current,output,Number(args[0])];break;
                    case "rotate":command=args[0].TryGetInt32(out var degrees)&&degrees is 90 or 180 or 270&&operationOptions.ValueKind!=JsonValueKind.Object?["rot",current,output,"d"+degrees]:["similarity",current,output,"--angle",Number(args[0])];break;
                    case "crop":command=["crop",current,output,Number(args[0]),Number(args[1]),Number(args[2]),Number(args[3])];break;
                    case "colourspace":command=["colourspace",current,output,Number(args[0])];break;
                    case "flip":command=["flip",current,output,"vertical"];break;
                    case "flop":command=["flip",current,output,"horizontal"];break;
                    case "sharpen":command=["sharpen",current,output];break;
                    case "flatten":command=["flatten",current,output];break;
                    case "strip":command=["copy",current,output+"[strip]"];break;
                    default:
                        var generic=new List<string>{operation.Name,current,output};foreach(var argument in args)if(argument.ValueKind!=JsonValueKind.Object)generic.Add(Number(argument));generic.AddRange(Options(operationOptions));command=generic.ToArray();break;
                }
                if(operation.Name is "resize" or "rotate" or "crop" or "colourspace" or "sharpen" or "flatten")command=command.Concat(Options(operationOptions)).ToArray();
                if(!await Process("vips",command,cancellationToken))return false;
                current=output;
                if(operation.Name.StartsWith("resize_to_",StringComparison.Ordinal)||operation.Name=="resize_and_pad")
                {
                    var sharpen=default(JsonElement);
                    if(operationOptions.ValueKind!=JsonValueKind.Object||!operationOptions.TryGetProperty("sharpen",out sharpen)||sharpen.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
                    {
                        var mask=destination+"-sharpen.mat";if(!intermediate.Contains(mask))intermediate.Add(mask);
                        var matrix="3 3 24 0\n-1 -1 -1\n-1 32 -1\n-1 -1 -1\n";
                        if(sharpen.ValueKind==JsonValueKind.Array)
                        {
                            var rows=sharpen.EnumerateArray().Select(row=>row.ValueKind==JsonValueKind.Array?row.EnumerateArray().ToArray():[row]).ToArray();
                            if(rows.Length==0||rows[0].Length==0||rows.Any(row=>row.Length!=rows[0].Length||row.Any(cell=>cell.ValueKind!=JsonValueKind.Number)))throw new ArgumentException("Invalid sharpen matrix");
                            matrix=rows[0].Length.ToString(CultureInfo.InvariantCulture)+" "+rows.Length.ToString(CultureInfo.InvariantCulture)+" 1 0\n"+string.Join('\n',rows.Select(row=>string.Join(' ',row.Select(Number))))+"\n";
                        }
                        await File.WriteAllTextAsync(mask,matrix,cancellationToken);
                        output=destination+"-"+intermediate.Count+".tiff";intermediate.Add(output);if(!await Process("vips",["conv",current,output,mask,"--precision","integer"],cancellationToken))return false;current=output;
                    }
                }
                if(operation.Name=="resize_and_pad")
                {
                    if(operationOptions.ValueKind==JsonValueKind.Object&&operationOptions.TryGetProperty("alpha",out var alpha)&&alpha.ValueKind==JsonValueKind.True)
                    {var withAlpha=destination+"-"+intermediate.Count+".tiff";intermediate.Add(withAlpha);if(!await Process("vips",["addalpha",current,withAlpha],cancellationToken))return false;current=withAlpha;}
                    var width=int.Parse((await Output("vipsheader",["-f","width",current],cancellationToken))!.Trim(),CultureInfo.InvariantCulture);
                    var height=int.Parse((await Output("vipsheader",["-f","height",current],cancellationToken))!.Trim(),CultureInfo.InvariantCulture);
                    var targetWidth=args[0].GetInt32();var targetHeight=args[1].GetInt32();
                    output=destination+"-"+intermediate.Count+".tiff";intermediate.Add(output);
                    var gravity=operationOptions.ValueKind==JsonValueKind.Object&&operationOptions.TryGetProperty("gravity",out var position)?position.GetString()!:"centre";
                    var padded=new List<string>{"gravity",current,output,gravity,targetWidth.ToString(CultureInfo.InvariantCulture),targetHeight.ToString(CultureInfo.InvariantCulture)};padded.AddRange(Options(operationOptions,"gravity","sharpen","size","crop","alpha"));
                    if(!await Process("vips",padded,cancellationToken))return false;
                    current=output;
                }
            }
            var saver=new List<string>();string? explicitSaver=null;
            if(transformations.TryGetProperty("saver",out var options))foreach(var option in options.EnumerateObject())
            {
                if(option.Name=="saver"){explicitSaver=option.Value.GetString();continue;}
                if(!System.Text.RegularExpressions.Regex.IsMatch(option.Name,"^[A-Za-z0-9_]+$"))throw new ArgumentException("Invalid saver option");
                var value=Number(option.Value);if(value.Contains('[')||value.Contains(']')||value.Contains(','))throw new ArgumentException("Invalid saver value");
                saver.Add((option.Name=="quality"?"Q":option.Name)+"="+value);
            }
            if(explicitSaver is not null){var command=new List<string>{explicitSaver+"save",current,destination};foreach(var option in saver){var pair=option.Split('=',2);command.AddRange(["--"+pair[0].Replace('_','-'),pair[1]]);}return await Process("vips",command,cancellationToken);}
            return await Process("vips",["copy",current,destination+(saver.Count==0?"":"["+string.Join(',',saver)+"]")],cancellationToken);
        }
        finally { foreach(var path in intermediate)File.Delete(path); }
    }
}
