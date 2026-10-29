using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Popcorn;

internal sealed record EmbeddedApiSource(string Id,string Label);
internal sealed record EmbeddedApiSelection(string[] Sources,string Notice);

internal static class EmbeddedApiSources
{
    internal static readonly EmbeddedApiSource[] Available={
        new("douban","豆瓣"),new("360","360"),new("tencent","腾讯视频"),new("youku","优酷"),
        new("iqiyi","爱奇艺"),new("imgo","芒果TV"),new("bilibili","哔哩哔哩"),new("migu","咪咕"),
        new("renren","人人视频"),new("hanjutv","韩剧TV"),new("sohu","搜狐"),new("leshi","乐视"),
        new("xigua","西瓜视频"),new("maiduidui","埋堆堆"),new("aiyifan","爱壹帆"),new("hongguo","红果"),
        new("bahamut","巴哈姆特"),new("animeko","Animeko")};
    internal static string[] Defaults=>new[]{"douban","360","renren","hanjutv"};
    internal static string[] Validate(string[] sources)
    {
        if(sources==null || sources.Length is <1 or >18 || sources.Distinct(StringComparer.Ordinal).Count()!=sources.Length ||
            sources.Any(id=>id==null || !Available.Any(item=>item.Id==id)))throw new ArgumentException("请选择 1 至 18 个不重复的受支持弹幕源。");
        return (string[])sources.Clone();
    }
    internal static EmbeddedApiSelection Load(string file)
    {
        if(!File.Exists(file))return new(Defaults,"");
        try
        {
            if(new FileInfo(file).Length>16384)throw new JsonException();
            using var doc=JsonDocument.Parse(File.ReadAllText(file));var value=doc.RootElement;
            if(value.ValueKind!=JsonValueKind.Object || value.EnumerateObject().Count()!=2 || value.GetProperty("schemaVersion").GetInt32()!=1)throw new JsonException();
            return new(Validate(value.GetProperty("sources").Deserialize<string[]>()),"");
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
        {return new(Defaults,"弹幕源偏好不可读取，暂用默认来源；原文件未改动，可在界面重新应用。");}
    }
    internal static void Save(string file,string[] sources)
    {
        sources=Validate(sources);Directory.CreateDirectory(Path.GetDirectoryName(file));
        string temporary=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temporary,JsonSerializer.Serialize(new{schemaVersion=1,sources}));File.Move(temporary,file,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
