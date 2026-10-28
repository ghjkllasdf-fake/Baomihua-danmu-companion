using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Popcorn;

public sealed record PreferenceReadResult(DanmakuSettings Settings,string Notice);

public static class SettingsPreferences
{
    public const int MaxFileBytes=65536;
    private sealed record Envelope(int SchemaVersion,DanmakuSettings Settings);
    private static readonly JsonSerializerOptions Json=new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true,
        UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,AllowDuplicateProperties=false,RespectRequiredConstructorParameters=true };
    public static PreferenceReadResult Load(string path)
    {
        try
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            // Read only the budget plus one sentinel byte, even if the source changes size while opening.
            var bytes=new byte[MaxFileBytes+1]; int count=0;
            while (count<bytes.Length)
            {
                int read=stream.Read(bytes,count,bytes.Length-count);
                if (read==0) break;
                count+=read;
            }
            if (count>MaxFileBytes) throw new InvalidDataException();
            int start=count>=3 && bytes[0]==239 && bytes[1]==187 && bytes[2]==191 ? 3 : 0;
            _=new UTF8Encoding(false,true).GetCharCount(bytes,start,count-start);
            using var doc=JsonDocument.Parse(bytes.AsMemory(start,count-start),new JsonDocumentOptions { AllowDuplicateProperties=false,MaxDepth=8 });
            var root=doc.RootElement;
            if (root.ValueKind!=JsonValueKind.Object) throw new JsonException();
            DanmakuSettings settings;
            if (root.TryGetProperty("schemaVersion",out var version))
            {
                if (version.ValueKind!=JsonValueKind.Number || !version.TryGetInt32(out int schema) || schema<1) throw new JsonException();
                if (schema!=1) return new(new(),"显示偏好格式版本不受支持，已使用默认值；原文件未修改，修改显示设置后将保存当前格式。");
                var envelope=root.Deserialize<Envelope>(Json)??throw new JsonException();
                settings=envelope.Settings??throw new JsonException();
            }
            else settings=root.Deserialize<DanmakuSettings>(Json)??throw new JsonException();
            settings.Validate(); return new(settings,"");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new(new(),""); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new(new(),"显示偏好无法读取，已使用默认值；原文件未修改，修改显示设置后才会保存。");
        }
    }
    public static string Serialize(DanmakuSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        return JsonSerializer.Serialize(new Envelope(1,settings),Json);
    }
}
