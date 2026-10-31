using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Popcorn;

internal sealed class UpdateVersion : IComparable<UpdateVersion>
{
    private readonly uint[] core;
    private readonly string[] pre;
    public string Text {get;}
    public bool Stable=>pre.Length==0;
    private UpdateVersion(string text,uint[] core,string[] pre){Text=text;this.core=core;this.pre=pre;}
    public static bool TryParse(string input,out UpdateVersion version)
    {
        version=null;
        if(input==null || input.Length is <5 or >96 || input.Any(char.IsWhiteSpace))return false;
        string text=input.StartsWith('v') ? input[1..] : input;
        if(!Regex.IsMatch(text,@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z"))return false;
        text=text.Split('+')[0];string[] parts=text.Split('-',2),numbers=parts[0].Split('.');
        var core=new uint[3];for(int i=0;i<3;i++)if(!uint.TryParse(numbers[i],out core[i]))return false;
        string[] pre=parts.Length==2 ? parts[1].Split('.') : Array.Empty<string>();
        if(pre.Any(p=>p.All(char.IsAsciiDigit) && p.Length>1 && p[0]=='0'))return false;
        version=new UpdateVersion(text,core,pre);return true;
    }
    public int CompareTo(UpdateVersion other)
    {
        for(int i=0;i<3;i++){int c=core[i].CompareTo(other.core[i]);if(c!=0)return c;}
        if(Stable || other.Stable)return Stable.CompareTo(other.Stable);
        for(int i=0;i<Math.Min(pre.Length,other.pre.Length);i++)
        {
            string a=pre[i],b=other.pre[i];bool an=a.All(char.IsAsciiDigit),bn=b.All(char.IsAsciiDigit);
            int c=an && bn ? a.Length!=b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a,b) : an!=bn ? an ? -1 : 1 : string.CompareOrdinal(a,b);
            if(c!=0)return c;
        }
        return pre.Length.CompareTo(other.pre.Length);
    }
    public static bool Newer(string a,string b)=>TryParse(a,out var x) && TryParse(b,out var y) && x.CompareTo(y)>0;
}

internal sealed record UpdateAsset(string Name,Uri Url,long Size,string Digest);
internal sealed record UpdateRelease(string Version,string Tag,string Notes,UpdateAsset Artifact,UpdateAsset Checksums);

internal static class UpdateCatalog
{
    public const string Repository="ghjkllasdf-fake/Baomihua-danmu-companion";
    public const long MaxDownload=512L*1024*1024;
    public static string ArtifactName(string version,bool includeApi,string distribution)
    {
        if(!UpdateVersion.TryParse(version,out var parsed) || parsed.Text!=version || distribution is not ("portable" or "installer"))
            throw new ArgumentException("不支持的更新版本或发行格式。");
        return "DanmuCompanion-"+version+"-win-x64"+(includeApi ? "-includeAPI" : "")+(distribution=="portable" ? "-portable.zip" : "-setup.exe");
    }
    internal static bool HashValid(string value)=>value!=null && Regex.IsMatch(value,@"\A[a-fA-F0-9]{64}\z");
    public static UpdateRelease Select(string json,string current,string channel,bool includeApi,string distribution)
    {
        if(channel is not ("beta" or "stable") || !UpdateVersion.TryParse(current,out _))throw new ArgumentException("更新通道或当前版本无效。");
        using var doc=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=16});
        if(doc.RootElement.ValueKind!=JsonValueKind.Array || doc.RootElement.GetArrayLength()>100)throw new InvalidDataException("GitHub 版本列表格式无效。");
        UpdateRelease selected=null;
        foreach(var release in doc.RootElement.EnumerateArray())
        {
            if(release.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("GitHub 版本记录无效。");
            string tag=release.GetProperty("tag_name").GetString();
            if(release.GetProperty("draft").GetBoolean() || !UpdateVersion.TryParse(tag,out var version) || !UpdateVersion.Newer(version.Text,current))continue;
            bool prerelease=release.GetProperty("prerelease").GetBoolean();
            if(version.Stable ? prerelease : !prerelease || channel=="stable" || !Regex.IsMatch(version.Text,@"-beta\.[1-9][0-9]*\z"))continue;
            if(selected!=null && !UpdateVersion.Newer(version.Text,selected.Version))continue;
            string name=ArtifactName(version.Text,includeApi,distribution);
            var assets=release.GetProperty("assets");
            if(assets.ValueKind!=JsonValueKind.Array || assets.GetArrayLength()>100)throw new InvalidDataException("GitHub 附件列表无效。");
            UpdateAsset artifact=FindAsset(assets,name,tag,MaxDownload),checksums=FindAsset(assets,"SHA256SUMS.txt",tag,65536);
            if(artifact==null || checksums==null)continue;
            string notes=release.TryGetProperty("body",out var body) && body.ValueKind==JsonValueKind.String ? body.GetString() : "";
            selected=new UpdateRelease(version.Text,tag,(notes??"")[..Math.Min(notes?.Length??0,16000)],artifact,checksums);
        }
        return selected;
    }
    private static UpdateAsset FindAsset(JsonElement assets,string name,string tag,long maximum)
    {
        var matches=assets.EnumerateArray().Where(a=>a.ValueKind==JsonValueKind.Object && a.TryGetProperty("name",out var n) && n.GetString()==name).ToArray();
        if(matches.Length!=1)return null;
        var asset=matches[0];long size=asset.GetProperty("size").GetInt64();
        string raw=asset.GetProperty("browser_download_url").GetString();
        string expected="https://github.com/"+Repository+"/releases/download/"+Uri.EscapeDataString(tag)+"/"+Uri.EscapeDataString(name);
        if(size<=0 || size>maximum || raw!=expected)throw new InvalidDataException("GitHub 更新附件地址或大小无效。");
        string digest=asset.TryGetProperty("digest",out var d) && d.ValueKind==JsonValueKind.String ? d.GetString() : "";
        if(!string.IsNullOrEmpty(digest))
        {
            if(!digest.StartsWith("sha256:",StringComparison.Ordinal) || !HashValid(digest[7..]))throw new InvalidDataException("GitHub 更新附件摘要无效。");
            digest=digest[7..].ToLowerInvariant();
        }
        return new UpdateAsset(name,new Uri(raw),size,digest??"");
    }
    public static string Checksum(string text,string name)
    {
        if(text==null || text.Length>65536)throw new InvalidDataException("更新校验清单过大。");
        string found=null;
        foreach(string line in text.TrimStart('\ufeff').Split('\n'))
        {
            string trimmed=line.TrimEnd('\r');if(string.IsNullOrWhiteSpace(trimmed))continue;
            var match=Regex.Match(trimmed,@"\A([a-fA-F0-9]{64}) [ *]([^\r\n]+)\z");
            if(!match.Success)throw new InvalidDataException("更新校验清单格式无效。");
            if(match.Groups[2].Value!=name)continue;
            if(found!=null)throw new InvalidDataException("更新校验清单包含重复附件。");
            found=match.Groups[1].Value.ToLowerInvariant();
        }
        return found??throw new InvalidDataException("没有找到当前版本的更新校验值。");
    }
}
