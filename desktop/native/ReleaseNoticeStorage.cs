using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Popcorn;

internal static class ReleaseNoticeStorage
{
    internal static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    internal static bool MetadataError(Exception error)=>error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or
        InvalidOperationException or ArgumentException or NotSupportedException or System.Security.SecurityException or
        System.Collections.Generic.KeyNotFoundException or FormatException or OverflowException;
    internal static void Check(bool valid,string message){if(!valid)throw new InvalidDataException(message);}
    internal static bool ValidInterval(int hours)=>hours is 24 or 168 or 720;
    internal static int ReadInterval(JsonElement root,int fallback=24,bool migrate=false)
    {
        if(!root.TryGetProperty("checkIntervalHours",out var value))return fallback;
        Check(value.ValueKind==JsonValueKind.Number && value.TryGetInt32(out int hours) && (ValidInterval(hours) || migrate && hours is 1 or 6 or 12),"检查频率无效。");
        return ValidInterval(value.GetInt32()) ? value.GetInt32() : 24;
    }
    internal static void Unique(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var properties=element.EnumerateObject().ToArray();
            Check(properties.Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==properties.Length,"JSON 包含重复属性。");
            foreach(var property in properties)Unique(property.Value);
        }
        else if(element.ValueKind==JsonValueKind.Array)foreach(var child in element.EnumerateArray())Unique(child);
    }
    internal static JsonDocument ReadJson(string path,int limit)
    {
        using var input=File.OpenRead(path);byte[] bytes=new byte[limit+1];int count=0;
        while(count<bytes.Length)
        {
            int read=input.Read(bytes,count,bytes.Length-count);if(read==0)break;count+=read;
        }
        Check(count<=limit,"版本或设置文件超过大小上限。");
        var doc=JsonDocument.Parse(bytes.AsMemory(0,count),new JsonDocumentOptions{MaxDepth=16});
        try{Unique(doc.RootElement);return doc;}catch{doc.Dispose();throw;}
    }
    internal static void Atomic(string path,byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {output.Write(bytes);output.Flush(true);}
            File.Move(temporary,path,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
