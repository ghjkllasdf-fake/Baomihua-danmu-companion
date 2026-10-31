using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Popcorn;

internal static class ResourceRetirement
{
    internal static bool Retired(string path)=>Regex.IsMatch(path,@"\A[a-z]{2}(?:-[A-Za-z0-9]+)?/[A-Za-z0-9_.-]+\.resources\.dll\z") &&
        !new[]{"en","zh-Hans","zh-Hant"}.Contains(path.Split('/')[0],StringComparer.OrdinalIgnoreCase);
    internal static int Apply(string root,string snapshot)
    {
        root=UpdatePackage.SafeRoot(root);snapshot=UpdatePackage.SafeRoot(snapshot);
        using var rootLock=new UpdateRootLock(root);UpdatePackage.Check(rootLock.Acquired,"程序仍在运行，已保留旧资源。");
        var current=UpdatePackage.Read(root);UpdatePackage.VerifyDirectory(root,current.Version,current.Variant);
        using var manifest=File.OpenRead(UpdatePackage.SafeFile(snapshot,"old-manifest.json"));byte[] raw=UpdatePackage.ReadLimited(manifest,4*1024*1024);
        using var json=JsonDocument.Parse(raw);var previous=UpdatePackage.Parse(raw,json.RootElement.GetProperty("version").GetString(),current.Variant);
        if(!UpdateVersion.Newer(current.Version,previous.Version))return 0;
        using var records=File.OpenRead(UpdatePackage.SafeFile(snapshot,"scope.txt"));string[] lines=new UTF8Encoding(false,true).GetString(UpdatePackage.ReadLimited(records,4*1024*1024)).Split(new[]{"\r\n","\n"},StringSplitOptions.RemoveEmptyEntries);
        UpdatePackage.Check(lines.Length is >0 and <=16385 && lines[0].StartsWith("retired-resources-v1|",StringComparison.Ordinal) &&
            string.Equals(UpdatePackage.SafeRoot(lines[0]["retired-resources-v1|".Length..]),root,StringComparison.OrdinalIgnoreCase),"旧资源清理记录与当前安装不匹配。");
        var entries=lines.Skip(1).Select(line=>line.Split('|')).ToArray();
        UpdatePackage.Check(entries.All(entry=>entry.Length==2 && entry[1].Length==48 && entry[1].All(char.IsAsciiHexDigit)) &&
            entries.Select(entry=>entry[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count()==entries.Length,"旧资源清理记录无效。");
        foreach(var entry in entries)
        {
            string path=entry[0].Replace('\\','/');UpdatePackage.SafeRelative(path);
            UpdatePackage.Check(Retired(path) && previous.Owned.Any(file=>file.Path==path) && !current.Owned.Any(file=>file.Path==path),"旧资源清理超出原版本所属范围。");
            UpdatePackage.SafeFile(root,path);
        }
        bool preserved=false;
        foreach(var entry in entries)
        {
            string relative=entry[0].Replace('\\','/'),file=UpdatePackage.SafeFile(root,relative);if(!File.Exists(file))continue;
            var expected=previous.Owned.Single(old=>old.Path==relative);
            if(!UpdateFileIdentity.RemoveMatching(file,entry[1].ToUpperInvariant(),expected)){preserved=true;continue;}
        }
        foreach(string culture in entries.Select(entry=>entry[0].Replace('\\','/').Split('/')[0]).Distinct())
        {
            string directory=UpdatePackage.SafeFile(root,culture);
            try{if(Directory.Exists(directory)){UpdatePackage.SafeRoot(directory);Directory.Delete(directory,false);}}catch(IOException){}catch(UnauthorizedAccessException){}
        }
        return preserved ? 2 : 0;
    }
}
