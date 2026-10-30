using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Popcorn;

public sealed record AppearanceSettings(string Skin="fluent",bool ReduceTransparency=false)
{
    public void Validate()
    {
        if(Skin is not ("fluent" or "glass" or "winui" or "studio"))throw new ArgumentException("皮肤标识无效。");
    }
}
public sealed record AppearanceReadResult(AppearanceSettings Settings,string Notice);
public sealed record AppearanceSnapshot(string Skin,bool ReduceTransparency,long Revision,string Backdrop,string Notice);

public static class AppearancePreferences
{
    public const int MaxFileBytes=4096;
    private sealed record Envelope(int SchemaVersion,AppearanceSettings Settings);
    private static readonly JsonSerializerOptions Json=new() {PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    public static AppearanceSettings Parse(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Object || value.EnumerateObject().Count()!=2 ||
            !value.TryGetProperty("skin",out var skin) || skin.ValueKind!=JsonValueKind.String ||
            !value.TryGetProperty("reduceTransparency",out var reduce) || reduce.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("皮肤设置格式无效。");
        var settings=new AppearanceSettings(skin.GetString(),reduce.GetBoolean());settings.Validate();return settings;
    }
    public static AppearanceReadResult Load(string path)
    {
        try
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            byte[] bytes=new byte[MaxFileBytes+1];int count=0,read;
            while(count<bytes.Length && (read=stream.Read(bytes,count,bytes.Length-count))>0)count+=read;
            if(count>MaxFileBytes)throw new InvalidDataException();
            int start=count>=3 && bytes[0]==239 && bytes[1]==187 && bytes[2]==191 ? 3 : 0;
            using var doc=JsonDocument.Parse(bytes.AsMemory(start,count-start),new JsonDocumentOptions {AllowDuplicateProperties=false,MaxDepth=4});
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object || root.EnumerateObject().Count()!=2 ||
                !root.TryGetProperty("schemaVersion",out var version) || !version.TryGetInt32(out int schema) || schema!=1 ||
                !root.TryGetProperty("settings",out var value))throw new InvalidDataException();
            return new(Parse(value),"");
        }
        catch(Exception ex) when(ex is FileNotFoundException or DirectoryNotFoundException){return new(new(),"");}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException or InvalidOperationException)
        {return new(new(),"外观偏好无法读取，已使用 A；原文件未修改，重新选择皮肤后才会保存。");}
    }
    public static void Save(string path,AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);settings.Validate();
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(new Envelope(1,settings),Json);
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes);output.Flush(true);}
            File.Move(temporary,path,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
