using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace Popcorn;

internal sealed record UpdateIdentity(string Version,string Variant,string Distribution,bool Supported)
{
    internal static string UninstallKey(string variant)=>@"Software\Microsoft\Windows\CurrentVersion\Uninstall\"+
        (variant=="includeAPI" ? "{D5C0CE54-F622-40E2-9F4A-D70D17C31BD2}_is1" : "{604952AE-033E-4832-9071-A9F995BA8C1D}_is1");
    internal static string DistributionFor(string root,string variant)
    {
        using var registry=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);using var key=registry.OpenSubKey(UninstallKey(variant));
        if(key!=null)
        {
            if(key.GetValue("InstallLocation") is not string location || !Path.IsPathFullyQualified(location))throw new InvalidDataException("安装登记缺少有效位置，请修复原安装。");
            if(string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)),Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),StringComparison.OrdinalIgnoreCase))
            {UpdatePackage.Check(File.Exists(Path.Combine(root,"unins000.exe")),"原安装缺少卸载组件，请修复安装后再更新。");return "installer";}
        }
        UpdatePackage.Check(!File.Exists(Path.Combine(root,"unins000.exe")),"当前目录与安装登记不匹配，未执行更新。");return "portable";
    }
    internal static UpdateIdentity Load(ApplicationPaths paths)
    {
        string raw=typeof(UpdateIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"";
        string version=UpdateVersion.TryParse(raw,out var parsed) ? parsed.Text : "";string variant=Directory.Exists(Path.Combine(paths.Root,"api")) ? "includeAPI" : "standard";
        try
        {
            var package=UpdatePackage.Read(paths.Root,version,variant);
            UpdatePackage.Check(package.Files.Any(f=>f.Path=="DanmuCompanion.Updater.exe") && File.Exists(Path.Combine(paths.Root,"DanmuCompanion.Updater.exe")),"当前包缺少原生更新组件。");
            return new UpdateIdentity(version,variant,DistributionFor(paths.Root,variant),true);
        }
        catch(Exception error) when(UpdateStorage.MetadataError(error))
        {return new UpdateIdentity(version,variant,"portable",false);}
    }
}

internal sealed record UpdatePreferences(bool AutoCheck,bool AutoDownload,string Channel,string CheckedAt,int CheckIntervalHours=6);
internal sealed record UpdateSnapshot(string State,string CurrentVersion,string AvailableVersion,string Variant,string Distribution,string Channel,
    bool AutoCheck,bool AutoDownload,string CheckedAt,long DownloadedBytes,long TotalBytes,string Notes,string Message,long Revision,bool Supported,bool CanInstall,string LastResult="",int CheckIntervalHours=6);
internal sealed record UpdateDownload(string File,string Checksum,long Size,string Version,string Variant,string Distribution,string Operation,string InventoryChecksum="",string ArchiveChecksum="",long ArchiveSize=0);

internal static class UpdateStorage
{
    internal static bool MetadataError(Exception error)=>error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or System.Collections.Generic.KeyNotFoundException or FormatException or OverflowException;
    internal static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    internal static JsonDocument ReadJson(string path,int limit)
    {using var input=File.OpenRead(path);var doc=JsonDocument.Parse(UpdatePackage.ReadLimited(input,limit));try{UpdatePackage.Unique(doc.RootElement);return doc;}catch{doc.Dispose();throw;}}
    internal static void Atomic(string path,byte[] bytes)
    {
        UpdatePackage.SafeRoot(Path.GetDirectoryName(path));Directory.CreateDirectory(Path.GetDirectoryName(path));string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes);output.Flush(true);}File.Move(temporary,path,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    internal static bool ValidInterval(int hours)=>hours is 1 or 6 or 12 or 24;
    internal static int ReadInterval(JsonElement root,int fallback=6)
    {
        if(!root.TryGetProperty("checkIntervalHours",out var value))return fallback;
        UpdatePackage.Check(value.ValueKind==JsonValueKind.Number && value.TryGetInt32(out int hours) && ValidInterval(hours),"更新检查频率无效。");
        return value.GetInt32();
    }
    internal static void Save(string path,UpdatePreferences preferences)
    {UpdatePackage.Check(ValidInterval(preferences.CheckIntervalHours),"更新检查频率无效。");Atomic(path,JsonSerializer.SerializeToUtf8Bytes(preferences,Json));}
    internal static UpdatePreferences Load(string path,string channel,out string notice)
    {
        notice="";
        try
        {
            using var input=File.OpenRead(path);using var doc=JsonDocument.Parse(UpdatePackage.ReadLimited(input,4096));var root=doc.RootElement;UpdatePackage.Unique(root);
            UpdatePackage.Check(root.EnumerateObject().Count()==(root.TryGetProperty("checkIntervalHours",out _) ? 5 : 4),"更新设置格式无效。");
            string savedChannel=root.GetProperty("channel").GetString(),stamp=root.GetProperty("checkedAt").GetString();
            UpdatePackage.Check(savedChannel is "beta" or "stable" && stamp!=null && stamp.Length<=64 && (stamp.Length==0 || DateTimeOffset.TryParse(stamp,out _)),"更新设置内容无效。");
            return new(root.GetProperty("autoCheck").GetBoolean(),root.GetProperty("autoDownload").GetBoolean(),savedChannel,stamp,ReadInterval(root));
        }
        catch(Exception error) when(error is FileNotFoundException or DirectoryNotFoundException){return new(true,false,channel,"");}
        catch(Exception error) when(MetadataError(error))
        {notice="更新设置无法读取，自动检查已停止，请重新保存设置。";return new(false,false,channel,"");}
    }
    internal static string CreateOperation(string parent,string purpose)
    {
        parent=UpdatePackage.SafeRoot(parent);Directory.CreateDirectory(parent);string id=Guid.NewGuid().ToString("N"),directory=Path.Combine(parent,id);
        UpdatePackage.Check(!Directory.Exists(directory),"更新操作目录已存在。");Directory.CreateDirectory(directory);UpdatePackage.SafeRoot(directory);
        using var output=new FileStream(Path.Combine(directory,"owner.json"),FileMode.CreateNew,FileAccess.Write,FileShare.None);
        JsonSerializer.Serialize(output,new{schemaVersion=1,component="github-updater",id,purpose});return directory;
    }
    internal static bool Owns(string parent,string directory)
    {
        try
        {
            parent=UpdatePackage.SafeRoot(parent);directory=UpdatePackage.SafeRoot(directory);
            if(!string.Equals(Path.GetDirectoryName(directory),parent,StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(directory),"N",out _))return false;
            using var input=File.OpenRead(Path.Combine(directory,"owner.json"));using var doc=JsonDocument.Parse(UpdatePackage.ReadLimited(input,4096));var record=doc.RootElement;UpdatePackage.Unique(record);
            return record.GetProperty("schemaVersion").GetInt32()==1 && record.GetProperty("component").GetString()=="github-updater" && record.GetProperty("id").GetString()==Path.GetFileName(directory);
        }
        catch(Exception error) when(MetadataError(error)){return false;}
    }
    internal static void RemoveOwned(string parent,string directory)
    {
        UpdatePackage.Check(Owns(parent,directory),"更新清理目标缺少所属证明。");
        void Remove(string root,int depth)
        {
            UpdatePackage.Check(depth<64,"更新清理目录过深。");UpdatePackage.SafeRoot(root);
            string[] entries=Directory.EnumerateFileSystemEntries(root).ToArray();
            string[] metadata={"owner.json","journal.json","next-manifest.json","original-files.json","registration.json","recovery.json","complete.json"};
            foreach(string entry in entries.OrderBy(entry=>depth==0 && metadata.Contains(Path.GetFileName(entry)) ? Path.GetFileName(entry)=="owner.json" ? 2 : 1 : 0))
            {
                var attributes=File.GetAttributes(entry);UpdatePackage.Check((attributes&FileAttributes.ReparsePoint)==0,"更新清理拒绝链接。");
                if((attributes&FileAttributes.Directory)!=0)Remove(entry,depth+1);else File.Delete(entry);
            }
            Directory.Delete(root);
        }
        Remove(directory,0);
    }
}
