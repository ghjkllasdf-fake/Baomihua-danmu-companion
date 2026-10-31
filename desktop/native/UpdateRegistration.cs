using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Popcorn;

internal sealed record UpdateOwnedDirectory(string Path,string Identity);
internal static class UpdateRegistration
{
    private sealed record Marker(string Root,string RootIdentity,string ManifestSha256,string Version,string Variant,string State,string MarkerIdentity,string SidecarIdentity,string SidecarSha256,UpdateOwnedDirectory[] Directories);
    private const string Header="github-initial-registration-v1";
    internal static string PathFor(string root)=>Path.Combine(UpdatePackage.SafeRoot(root),".DanmuCompanion-registration.txt");
    internal static bool Legacy(string version)=>UpdateVersion.Newer("0.1.1-beta.7",version);
    private static string Digest(UpdatePackage package)=>Convert.ToHexString(SHA256.HashData(package.Manifest));
    private static Marker Read(string root,UpdateDirectories directories,out byte[] bytes,out string identity)
    {
        using var input=UpdateFileIdentity.OpenGuard(directories.File(root,Path.GetFileName(PathFor(root))));
        identity=UpdateFileIdentity.Read(input);bytes=UpdatePackage.ReadLimited(input,4194304);
        string[] lines=new UTF8Encoding(false,true).GetString(bytes).TrimEnd('\r','\n').Split('\n').Select(line=>line.TrimEnd('\r')).ToArray();
        UpdatePackage.Check(lines.Length is >=10 and <=32778 && lines[0]==Header,"应用初始登记格式无效，未重新登记。");
        var owned=lines.Skip(10).Select(line=>{string[] fields=line.Split('|');UpdatePackage.Check(fields.Length==3 && fields[0]=="D","目录身份记录无效。");return new UpdateOwnedDirectory(fields[1],fields[2]);}).ToArray();ValidateDirectories(owned);
        var marker=new Marker(lines[1],lines[2],lines[3],lines[4],lines[5],lines[6],lines[7],lines[8]=="-" ? "" : lines[8],lines[9]=="-" ? "" : lines[9],owned);
        UpdatePackage.Check(
            marker.Root==UpdatePackage.SafeRoot(root).ToUpperInvariant() && marker.RootIdentity==directories.Identity(root) && marker.MarkerIdentity==identity &&
            marker.ManifestSha256?.Length==64 && marker.ManifestSha256.All(char.IsAsciiHexDigit) && UpdateVersion.TryParse(marker.Version,out _) &&
            marker.Variant is "standard" or "includeAPI" && marker.State is "initializing" or "registered" &&
            (marker.State=="initializing" ? marker.SidecarIdentity=="" && marker.SidecarSha256=="" : marker.SidecarIdentity.Length==48 && marker.SidecarIdentity.All(char.IsAsciiHexDigit) && marker.SidecarSha256.Length==64 && marker.SidecarSha256.All(char.IsAsciiHexDigit)),
            "应用初始登记标记失效，未重新信任当前文件。请保留原目录并使用新的空目录安装／解压。");return marker;
    }
    internal static void RequireReady(string root,UpdatePackage package)
    {
        using var directories=new UpdateDirectories();directories.Hold(root);var marker=Read(root,directories,out _,out _);
        using var sidecar=UpdateFileIdentity.OpenGuard(UpdateInstalledOwnership.PathFor(root));
        UpdatePackage.Check(sidecar.Length<=4194304,"应用文件身份登记超过预算。");string hash=UpdatePackage.Hash(sidecar,sidecar.Length);sidecar.Position=0;
        var files=UpdateInstalledOwnership.Read(root,sidecar);
        UpdatePackage.Check(marker.State=="registered" && marker.ManifestSha256==Digest(package) && marker.Version==package.Version && marker.Variant==package.Variant &&
            marker.SidecarIdentity==UpdateFileIdentity.Read(sidecar) && marker.SidecarSha256.Equals(hash,StringComparison.OrdinalIgnoreCase) && files.Length==package.Owned.Length &&
            files.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(package.Owned.Select(f=>f.Path)),
            "应用文件身份登记缺失、不完整或未完成，未执行更新。请保留原目录；安装版卸载登记后安装到新的空目录，Portable 完整解压到新的空目录。");
    }
    internal static void ValidateDirectories(UpdateOwnedDirectory[] records)
    {
        UpdatePackage.Check(records!=null && records.Length<=32768 && records.All(record=>record!=null && record.Identity?.Length==48 && record.Identity.All(char.IsAsciiHexDigit)),"目录身份记录无效。");
        foreach(var record in records)UpdatePackage.SafeRelative(record.Path);
        UpdatePackage.Check(records.Select(record=>record.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()==records.Length,"目录身份记录重复。");
    }
    internal static UpdateOwnedDirectory[] OwnedDirectories(string root)
    {using var directories=new UpdateDirectories();directories.Hold(root);return Read(root,directories,out _,out _).Directories;}
    internal static string[] Parents(UpdatePackage package)
    {
        var parents=new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in package.Owned){string parent=file.Path;int divider;while((divider=parent.LastIndexOf('/'))>=0){parent=parent[..divider];parents.Add(parent);}}
        return parents.ToArray();
    }
    internal static UpdateOwnedDirectory[] BaselineDirectories(string root,UpdatePackage package)
    {using var directories=new UpdateDirectories();return Parents(package).Select(relative=>new UpdateOwnedDirectory(relative,directories.Identity(Path.Combine(root,relative)))).ToArray();}
    internal static void CheckPackageBudget(string root,UpdatePackage package)
    {
        var parents=Parents(package);
        long header=Encoding.UTF8.GetByteCount(UpdatePackage.SafeRoot(root).ToUpperInvariant())+768;
        UpdatePackage.Check(parents.Length<=32768 && header+parents.Sum(path=>(long)Encoding.UTF8.GetByteCount(path)+53)<=4194304 &&
            header+package.Owned.Sum(file=>(long)Encoding.UTF8.GetByteCount(file.Path)+51)<=4194304,"程序文件身份登记超过预算，未覆盖。");
    }
    internal static void Write(string root,UpdatePackage package,string state,string expectedSidecar="",UpdateMetadataAuthority authority=null,UpdateOwnedDirectory[] created=null,Action<FileStream> beforeWrite=null)
    {
        UpdatePackage.Check(state is "initializing" or "registered","初始登记状态无效。");
        using var directories=new UpdateDirectories();directories.Hold(root);string path=PathFor(root),identity="";byte[] previous=null;
        Marker existing=null;
        if(File.Exists(path))
        {
            existing=Read(root,directories,out previous,out identity);
            if(state=="initializing" && existing.State==state && existing.ManifestSha256==Digest(package) && existing.Version==package.Version && existing.Variant==package.Variant)return;
        }
        else UpdatePackage.Check(!Directory.Exists(path),"初始登记位置被外来目录占用，未覆盖。");
        string temporary=directories.File(root,".DanmuCompanion-registration-"+Guid.NewGuid().ToString("N")+".tmp");
        string temporaryIdentity="";
        try
        {
            using var sidecar=state=="registered" ? UpdateFileIdentity.OpenGuard(UpdateInstalledOwnership.PathFor(root)) : null;
            string sidecarHash="";
            if(sidecar!=null)
            {UpdatePackage.Check(UpdateFileIdentity.Read(sidecar)==expectedSidecar && sidecar.Length<=4194304,"新登记文件已被外来对象替换，未完成登记。");sidecarHash=UpdatePackage.Hash(sidecar,sidecar.Length).ToUpperInvariant();}
            using(var output=UpdateFileIdentity.CreateOwned(temporary))
            {
                temporaryIdentity=UpdateFileIdentity.Read(output);
                beforeWrite?.Invoke(output);
                string normalized=UpdatePackage.SafeRoot(root).ToUpperInvariant();UpdatePackage.Check(!normalized.Any(char.IsControl),"初始登记目录不能含控制字符。");
                var historical=(existing?.Directories??authority?.OriginalDirectories??Array.Empty<UpdateOwnedDirectory>()).ToDictionary(record=>record.Path,StringComparer.OrdinalIgnoreCase);
                var newDirectories=(created??Array.Empty<UpdateOwnedDirectory>()).ToDictionary(record=>record.Path,StringComparer.OrdinalIgnoreCase);
                var parents=new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var file in package.Owned)
                {string parent=Path.GetDirectoryName(Path.Combine(root,file.Path));while(!parent.Equals(root,StringComparison.OrdinalIgnoreCase)){parents.Add(parent);parent=Path.GetDirectoryName(parent);}}
                var records=new System.Collections.Generic.List<UpdateOwnedDirectory>();
                foreach(string parent in parents.OrderByDescending(value=>value.Length))
                {
                    string relative=Path.GetRelativePath(root,parent).Replace(Path.DirectorySeparatorChar,'/');string id=directories.Identity(parent);
                    if(historical.TryGetValue(relative,out var old))records.Add(old);
                    else if(authority==null && existing==null || newDirectories.TryGetValue(relative,out var owned) && owned.Identity==id)records.Add(new(relative,id));
                }
                ValidateDirectories(records.ToArray());
                byte[] encoded=Encoding.UTF8.GetBytes(string.Join("\r\n",new[]{Header,normalized,directories.Identity(root),Digest(package),package.Version,package.Variant,state,UpdateFileIdentity.Read(output),expectedSidecar=="" ? "-" : expectedSidecar,sidecarHash=="" ? "-" : sidecarHash}.Concat(records.Select(record=>"D|"+record.Path+"|"+record.Identity))));
                UpdatePackage.Check(encoded.Length<=4194304,"初始登记标记超过预算。");output.Write(encoded);output.Flush(true);
                if(authority!=null)authority.Replace("marker",output);
                else
                {
                    if(previous!=null)UpdatePackage.Check(UpdateFileIdentity.RemoveMatching(path,identity,new UpdateFile(Path.GetFileName(path),previous.Length,Convert.ToHexString(SHA256.HashData(previous)))),"初始登记标记被替换或占用，未覆盖。");
                    UpdateFileIdentity.Rename(output,path);
                }
            }
        }
        finally
        {
            // The first failed publication is itself durable stop evidence, not disposable scratch data.
            if(temporaryIdentity!="" && File.Exists(temporary) && (previous!=null || state=="registered" || authority!=null))UpdateFileIdentity.RemoveMatching(temporary,temporaryIdentity);
        }
    }
    internal static void InitializePortable(string root,Action begun=null,Action<FileStream> beforeFirstWrite=null)
    {
        root=UpdatePackage.SafeRoot(root);var package=UpdatePackage.Read(root);if(Legacy(package.Version))return;CheckPackageBudget(root,package);
        string name=@"Local\DanmuCompanion-initialize-"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.Unicode.GetBytes(root.ToUpperInvariant())));
        using var mutex=new Mutex(false,name);bool acquired;try{acquired=mutex.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
        UpdatePackage.Check(acquired,"应用初始登记正在进行，未重复登记。");
        try
        {
            if(File.Exists(PathFor(root)) || File.Exists(UpdateInstalledOwnership.PathFor(root))){RequireReady(root,package);return;}
            UpdatePackage.Check(!Directory.EnumerateFileSystemEntries(root,".DanmuCompanion-registration-*.tmp").Any(),"存在未完成的初始登记，未重新信任当前文件。请完整解压到新的空目录。");
            // Only first use establishes a baseline. Later missing/partial receipts never re-bootstrap.
            using var guard=new UpdatePayloadGuard(root,package);Write(root,package,"initializing",beforeWrite:beforeFirstWrite);begun?.Invoke();
            UpdateInstalledOwnership.Save(root,guard.Identities,expectedIdentity:"");
        }
        finally{mutex.ReleaseMutex();}
    }
    internal static void Probe(string root)
    {
        using var directories=new UpdateDirectories();directories.Hold(root,true);string path=PathFor(root);
        if(File.Exists(path))
        {
            Read(root,directories,out _,out _);UpdatePackage.Check(UpdateFileIdentity.CanReplace(path),"应用初始登记标记被占用，未覆盖。");
        }
        else UpdatePackage.Check(!Directory.Exists(path),"应用初始登记位置被外来目录占用，未覆盖。");
        string probe=directories.File(root,".DanmuCompanion-registration-probe-"+Guid.NewGuid().ToString("N")+".tmp");
        try{using(var output=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.WriteByte(0);output.Flush(true);}}
        finally{if(File.Exists(probe))File.Delete(probe);}
    }
}
