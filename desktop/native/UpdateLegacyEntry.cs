using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Popcorn;

internal static class UpdateLegacyEntry
{
    internal static string PathFor(ApplicationPaths paths)=>Path.Combine(UpdateInstalledOwnership.ParentFor(paths.Root),"Recover-Danmu-"+
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(UpdatePackage.SafeRoot(paths.Root).ToUpperInvariant())))[..12]+".lnk");
    internal static void Retire(ApplicationPaths paths)
    {
        DisableKnownRecords(paths);
        object shell=null,link=null;
        try
        {
            string path=PathFor(paths);if(!File.Exists(path))return;UpdatePackage.SafeFile(Path.GetDirectoryName(path),Path.GetFileName(path));
            using var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);long length=held.Length;
            UpdatePackage.Check(length<=65536,"历史快捷方式过大，未清理。");string hash=UpdatePackage.Hash(held,length),identity=UpdateFileIdentity.Read(path);
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));link=shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{path});
            object Get(string name)=>link.GetType().InvokeMember(name,BindingFlags.GetProperty,null,link,null);
            string target=Get("TargetPath") as string,operation=target==null ? null : Path.GetDirectoryName(Path.GetDirectoryName(target));
            if(operation==null || !UpdateStorage.Owns(Path.Combine(paths.ProfileRoot,"updates"),operation) ||
                !string.Equals(target,Path.Combine(operation,"helper","DanmuCompanion.Updater.exe"),StringComparison.OrdinalIgnoreCase) ||
                Get("Description") as string!="DanmuCompanion owned update recovery" || Get("Arguments") as string!="")return;
            string recordPath=Path.Combine(operation,File.Exists(Path.Combine(operation,"recovery.json")) ? "recovery.json" : "legacy-disabled-recovery.json");
            using var legacy=UpdateStorage.ReadJson(recordPath,16384);var job=legacy.RootElement.GetProperty("job");
            if(!string.Equals(UpdatePackage.SafeRoot(job.GetProperty("applicationRoot").GetString()),paths.Root,StringComparison.OrdinalIgnoreCase))return;
            held.Dispose();UpdateFileIdentity.RemoveMatching(path,identity,new UpdateFile(Path.GetFileName(path),length,hash));
            // Only the proven shortcut is retired. Never execute the helper or delete historical backup data.
        }
        catch(Exception error) when(UpdateStorage.MetadataError(error) || error is COMException or TargetInvocationException or System.ComponentModel.Win32Exception){}
        finally{if(link!=null)Marshal.FinalReleaseComObject(link);if(shell!=null)Marshal.FinalReleaseComObject(shell);}
    }
    private static void DisableKnownRecords(ApplicationPaths paths)
    {
        string cache=Path.Combine(paths.ProfileRoot,"updates");if(!Directory.Exists(cache))return;
        try
        {
            int count=0;
            foreach(string directory in Directory.EnumerateDirectories(cache))
            {
                if(++count>4096)break;
                try
                {
                    if(!UpdateStorage.Owns(cache,directory))continue;
                    string source=UpdatePackage.SafeFile(directory,"recovery.json"),disabled=UpdatePackage.SafeFile(directory,"legacy-disabled-recovery.json");
                    if(!File.Exists(source))continue;
                    using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);byte[] raw=UpdatePackage.ReadLimited(input,16384);
                    using var record=System.Text.Json.JsonDocument.Parse(raw);UpdatePackage.Unique(record.RootElement);var job=record.RootElement.GetProperty("job");
                    if(!string.Equals(UpdatePackage.SafeRoot(job.GetProperty("applicationRoot").GetString()),paths.Root,StringComparison.OrdinalIgnoreCase) ||
                        UpdateMaintenance.Running(record.RootElement,Path.Combine(directory,"helper","DanmuCompanion.Updater.exe")))continue;
                    string identity=UpdateFileIdentity.Read(source);
                    if(File.Exists(disabled))
                    {
                        using var saved=File.OpenRead(disabled);UpdatePackage.Check(UpdatePackage.ReadLimited(saved,16384).AsSpan().SequenceEqual(raw),"历史停用记录已改变，未覆盖。");
                    }
                    else using(var output=new FileStream(disabled,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(raw);output.Flush(true);}
                    input.Dispose();UpdateFileIdentity.RemoveMatching(source,identity,new UpdateFile("recovery.json",raw.Length,Convert.ToHexString(SHA256.HashData(raw))));
                }
                catch(Exception error) when(UpdateStorage.MetadataError(error) || error is System.ComponentModel.Win32Exception){}
            }
        }
        catch(Exception error) when(UpdateStorage.MetadataError(error)){}
    }
}
