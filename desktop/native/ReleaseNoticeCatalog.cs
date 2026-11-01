using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Popcorn;

internal sealed record ReleaseNoticeIdentity(string Version,string Variant,string Distribution,bool Supported)
{
    internal static ReleaseNoticeIdentity Load(ApplicationPaths paths)
    {
        string raw=typeof(ReleaseNoticeIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"";
        string version=ReleaseVersion.TryParse(raw,out var parsed) ? parsed.Text : "";
        string variant=Directory.Exists(Path.Combine(paths.Root,"api")) ? "includeAPI" : "standard";
        string distribution=File.Exists(Path.Combine(paths.Root,"unins000.exe")) ? "installer" : "portable";
        try
        {
            using var manifest=ReleaseNoticeStorage.ReadJson(Path.Combine(paths.Root,"manifest.json"),4*1024*1024);
            var header=manifest.RootElement;
            ReleaseNoticeStorage.Check(header.GetProperty("schemaVersion").GetInt32()==1 && header.GetProperty("version").GetString()==version &&
                !header.GetProperty("sourceDirty").GetBoolean(),"当前发行版本信息无效。");
            variant=header.GetProperty("variant").GetString();
            ReleaseNoticeStorage.Check(variant is "standard" or "includeAPI","当前发行版类型无效。");
            return new(version,variant,distribution,version.Length>0);
        }
        catch(Exception error) when(ReleaseNoticeStorage.MetadataError(error)){return new(version,variant,distribution,false);}
    }
}

internal sealed record ReleaseNotice(string Version,string Tag,string Notes)
{internal string Page=>ReleaseNoticeCatalog.Page(Tag);}
internal sealed record ReleaseNoticePreferences(bool AutoCheck,string Channel,string CheckedAt,int CheckIntervalHours=24);
internal sealed record ReleaseNoticeSnapshot(string State,string CurrentVersion,string AvailableVersion,string Variant,string Distribution,string Channel,
    bool AutoCheck,string CheckedAt,string Notes,string Message,long Revision,bool Supported,int CheckIntervalHours,string ReleasePage);

internal static class ReleaseNoticeCatalog
{
    internal const string Repository="ghjkllasdf-fake/Baomihua-danmu-companion";
    internal static string Page(string tag=null)=>"https://github.com/"+Repository+"/releases"+(tag==null ? "" : "/tag/"+Uri.EscapeDataString(tag));
    internal static ReleaseNotice Select(JsonElement list,string current,string channel)
    {
        ReleaseNoticeStorage.Check(channel is "stable" or "beta" && ReleaseVersion.TryParse(current,out _),"当前版本或更新通道无效。");
        ReleaseNoticeStorage.Unique(list);
        ReleaseNoticeStorage.Check(list.ValueKind==JsonValueKind.Array && list.GetArrayLength()<=100,"GitHub 版本列表无效。");
        ReleaseNotice selected=null;
        foreach(var release in list.EnumerateArray())
        {
            ReleaseNoticeStorage.Check(release.ValueKind==JsonValueKind.Object,"GitHub 版本记录无效。");
            string tag=release.GetProperty("tag_name").GetString();
            if(release.GetProperty("draft").GetBoolean() || !ReleaseVersion.TryParse(tag,out var version) || !ReleaseVersion.Newer(version.Text,current))continue;
            if(tag!=version.Text && tag!="v"+version.Text)continue;
            bool prerelease=release.GetProperty("prerelease").GetBoolean();
            if(version.Stable ? prerelease : !prerelease || channel=="stable" || !Regex.IsMatch(version.Text,@"-beta\.[1-9][0-9]*\z"))continue;
            if(selected!=null && !ReleaseVersion.Newer(version.Text,selected.Version))continue;
            string notes=release.TryGetProperty("body",out var body) && body.ValueKind==JsonValueKind.String ? body.GetString() : "";
            selected=new(version.Text,tag,(notes??"")[..Math.Min(notes?.Length??0,16000)]);
        }
        return selected;
    }
    internal static ReleaseNoticePreferences LoadPreferences(string path,string channel,out string notice)
    {
        notice="";
        try
        {
            using var doc=ReleaseNoticeStorage.ReadJson(path,4096);var root=doc.RootElement;
            bool legacy=root.TryGetProperty("autoDownload",out var ignored);
            ReleaseNoticeStorage.Check(root.ValueKind==JsonValueKind.Object && root.EnumerateObject().Count()==3+(legacy ? 1 : 0)+(root.TryGetProperty("checkIntervalHours",out _) ? 1 : 0),"更新提醒设置无效。");
            if(legacy)ReleaseNoticeStorage.Check(ignored.ValueKind is JsonValueKind.True or JsonValueKind.False,"历史更新设置无效。");
            string saved=root.GetProperty("channel").GetString(),stamp=root.GetProperty("checkedAt").GetString();
            ReleaseNoticeStorage.Check(saved is "beta" or "stable" && stamp!=null && stamp.Length<=64 && (stamp=="" || DateTimeOffset.TryParse(stamp,out _)),"更新提醒设置内容无效。");
            return new(root.GetProperty("autoCheck").GetBoolean(),saved,stamp,ReleaseNoticeStorage.ReadInterval(root,migrate:true));
        }
        catch(Exception error) when(error is FileNotFoundException or DirectoryNotFoundException){return new(true,channel,"");}
        catch(Exception error) when(ReleaseNoticeStorage.MetadataError(error))
        {notice="更新提醒设置无法读取，自动检查已停止，请重新保存设置。";return new(false,channel,"");}
    }
    internal static void SavePreferences(string path,ReleaseNoticePreferences preferences)=>
        ReleaseNoticeStorage.Atomic(path,JsonSerializer.SerializeToUtf8Bytes(preferences,ReleaseNoticeStorage.Json));
}
