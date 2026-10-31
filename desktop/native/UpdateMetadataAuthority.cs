using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Popcorn;

internal sealed class UpdateMetadataAuthority
{
    private sealed record Evidence(string Key,long Length,string Sha256,string Identity);
    private sealed record Snapshot(string Root,string RootIdentity,Evidence[] Files,UpdateOwnedDirectory[] Directories);
    private readonly string root,directory;
    private readonly Snapshot original;
    private System.Collections.Generic.Dictionary<string,System.Collections.Generic.HashSet<string>> knownDirectories;
    private static readonly string[] Keys={"marker","sidecar"};
    internal bool HadMarker=>original.Files[0].Identity!="";
    internal UpdateOwnedDirectory[] OriginalDirectories=>original.Directories;
    internal Action<string> AfterCommit;
    internal Action<FileStream> BeforeRename;
    internal Action CleanupRootHeld {get;set;}
    internal UpdateMetadataAuthority(string root,string directory,bool capture,UpdatePackage baseline=null)
    {
        this.root=UpdatePackage.SafeRoot(root);this.directory=UpdatePackage.SafeRoot(directory);
        using var directories=new UpdateDirectories();directories.Hold(root,capture);string path=Path.Combine(directory,"metadata-original.json");
        if(capture)
        {
            UpdatePackage.Check(!File.Exists(path),"覆盖操作已存在初始元数据记录。");
            original=new(root.ToUpperInvariant(),directories.Identity(root),Keys.Select(Fingerprint).ToArray(),File.Exists(UpdateRegistration.PathFor(root)) ? UpdateRegistration.OwnedDirectories(root) : baseline!=null ? UpdateRegistration.BaselineDirectories(root,baseline) : Array.Empty<UpdateOwnedDirectory>());
            byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(original,UpdateStorage.Json);UpdatePackage.Check(bytes.Length<=8388608,"初始元数据记录超过预算，未覆盖。");UpdateStorage.Atomic(path,bytes);
        }
        else
        {
            using var input=UpdateFileIdentity.OpenGuard(UpdatePackage.SafeFile(directory,Path.GetFileName(path)));using var json=JsonDocument.Parse(UpdatePackage.ReadLimited(input,8388608));UpdatePackage.Unique(json.RootElement);original=json.RootElement.Deserialize<Snapshot>(UpdateStorage.Json);
            UpdatePackage.Check(original!=null && original.Root==root.ToUpperInvariant() && original.RootIdentity?.Length==48 && original.RootIdentity.All(char.IsAsciiHexDigit) &&
                original.Files?.Length==2 && original.Files.Select(f=>f?.Key).SequenceEqual(Keys),"覆盖操作的初始元数据身份无效。");
            foreach(var evidence in original.Files)Valid(evidence,evidence.Key);
            UpdateRegistration.ValidateDirectories(original.Directories);
        }
    }
    private string Target(string key)=>key=="marker" ? UpdateRegistration.PathFor(root) : key=="sidecar" ? UpdateInstalledOwnership.PathFor(root) : throw new InvalidDataException("未知元数据类型。");
    private static void Valid(Evidence value,string key)
    {
        UpdatePackage.Check(value!=null && value.Key==key && value.Length is >=0 and <=4194304 &&
            (value.Identity=="" ? value.Length==0 && value.Sha256=="" : value.Identity?.Length==48 && value.Identity.All(char.IsAsciiHexDigit) && value.Sha256?.Length==64 && value.Sha256.All(char.IsAsciiHexDigit)),"覆盖操作的元数据证明无效。");
    }
    private Evidence Fingerprint(string key)
    {
        string target=Target(key);UpdatePackage.Check(!Directory.Exists(target),"元数据位置被外来目录占用，未覆盖。");
        if(!File.Exists(target))return new(key,0,"","");
        using var input=UpdateFileIdentity.OpenGuard(UpdatePackage.SafeFile(Path.GetDirectoryName(target),Path.GetFileName(target)));
        UpdatePackage.Check(input.Length<=4194304,"元数据超过预算。");return new(key,input.Length,UpdatePackage.Hash(input,input.Length),UpdateFileIdentity.Read(input));
    }
    private Evidence ReadProof(string key,string slot)
    {
        string path=Path.Combine(directory,"metadata-"+key+"."+slot+".json");if(!File.Exists(path))return null;
        using var input=UpdateFileIdentity.OpenGuard(UpdatePackage.SafeFile(directory,Path.GetFileName(path)));using var json=JsonDocument.Parse(UpdatePackage.ReadLimited(input,4096));UpdatePackage.Unique(json.RootElement);var value=json.RootElement.Deserialize<Evidence>(UpdateStorage.Json);Valid(value,key);return value;
    }
    private void CheckRoot()
    {
        using var directories=new UpdateDirectories();directories.Hold(root);
        UpdatePackage.Check(directories.Identity(root)==original.RootIdentity,"覆盖操作的原目录对象已被替换，未覆盖。");
    }
    private Evidence Current(string key)
    {
        var actual=Fingerprint(key);var first=original.Files.Single(f=>f.Key==key);var current=ReadProof(key,"current");var pending=ReadProof(key,"pending");
        UpdatePackage.Check(actual.Identity=="" || actual==first || actual==current || actual==pending,"元数据被外来对象替换或改变，未覆盖，也未重新登记当前对象。");return actual;
    }
    internal void Validate()
    {CheckRoot();foreach(string key in Keys)Current(key);ValidateDirectories();}
    internal void RecordDirectory(string path,string identity)
    {
        if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return;
        string relative=Path.GetRelativePath(root,path).Replace(Path.DirectorySeparatorChar,'/');UpdatePackage.SafeRelative(relative);
        string digest=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.Unicode.GetBytes(relative.ToUpperInvariant())))[..16];
        string file=Path.Combine(directory,"directory-"+digest+"-"+identity+".json");
        UpdateStorage.Atomic(file,JsonSerializer.SerializeToUtf8Bytes(new UpdateOwnedDirectory(relative,identity),UpdateStorage.Json));
        if(knownDirectories!=null)
        {if(!knownDirectories.TryGetValue(relative,out var ids))knownDirectories.Add(relative,ids=new());ids.Add(identity);}
    }
    internal UpdateOwnedDirectory[] CreatedDirectories()
    {
        var paths=Directory.EnumerateFiles(directory,"directory-*.json").Take(65537).ToArray();UpdatePackage.Check(paths.Length<=65536,"目录创建记录过多。");
        return paths.Select(path=>
        {
            using var input=UpdateFileIdentity.OpenGuard(UpdatePackage.SafeFile(directory,Path.GetFileName(path)));using var json=JsonDocument.Parse(UpdatePackage.ReadLimited(input,4096));UpdatePackage.Unique(json.RootElement);
            var record=json.RootElement.Deserialize<UpdateOwnedDirectory>(UpdateStorage.Json);UpdateRegistration.ValidateDirectories(new[]{record});
            string digest=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.Unicode.GetBytes(record.Path.ToUpperInvariant())))[..16];
            UpdatePackage.Check(Path.GetFileName(path)=="directory-"+digest+"-"+record.Identity+".json","目录创建记录与所属路径不符。");return record;
        }).ToArray();
    }
    private void ValidateDirectories()
    {
        var created=CreatedDirectories();using var directories=new UpdateDirectories();
        foreach(var group in original.Directories.Concat(created).GroupBy(record=>record.Path,StringComparer.OrdinalIgnoreCase))
        {
            string path=Path.Combine(root,group.Key);if(!Directory.Exists(path)){UpdatePackage.Check(!File.Exists(path),"原程序目录被外来文件占用。");continue;}
            string identity=directories.Identity(path);UpdatePackage.Check(group.Any(record=>record.Identity==identity),"原程序目录已被外来对象替换，未覆盖。");
        }
    }
    internal void PrepareDirectories(UpdateDirectories directories,UpdatePackage package)
    {
        Validate();knownDirectories=original.Directories.Concat(CreatedDirectories()).GroupBy(record=>record.Path,StringComparer.OrdinalIgnoreCase).ToDictionary(group=>group.Key,group=>group.Select(record=>record.Identity).ToHashSet(),StringComparer.OrdinalIgnoreCase);
        foreach(string relative in UpdateRegistration.Parents(package).OrderBy(value=>value.Length))
        {
            string path=Path.Combine(root,relative);directories.Hold(path,true);string id=directories.Identity(path);
            UpdatePackage.Check(knownDirectories.TryGetValue(relative,out var ids) && ids.Contains(id),"程序目录缺少可验证的原有／创建登记，未覆盖。请保留原目录；安装版卸载登记后在新的空目录安装，Portable 完整解压到新的空目录。");
        }
    }
    internal void ValidateIncomingDirectories(UpdatePackage package)
    {
        Validate();knownDirectories=original.Directories.Concat(CreatedDirectories()).GroupBy(record=>record.Path,StringComparer.OrdinalIgnoreCase).ToDictionary(group=>group.Key,group=>group.Select(record=>record.Identity).ToHashSet(),StringComparer.OrdinalIgnoreCase);
        using var directories=new UpdateDirectories();
        foreach(string relative in UpdateRegistration.Parents(package))
        {
            string path=Path.Combine(root,relative);UpdatePackage.Check(!File.Exists(path),"程序目录被外来文件占用，未覆盖。");
            if(!Directory.Exists(path))continue;
            UpdatePackage.Check(knownDirectories.TryGetValue(relative,out var ids) && ids.Contains(directories.Identity(path)),"程序目录缺少可验证的原有／创建登记，未覆盖。请保留原目录；安装版卸载登记后在新的空目录安装，Portable 完整解压到新的空目录。");
        }
    }
    internal string DirectoryIdentity(string relative)
    {
        string path=Path.Combine(root,relative);if(!Directory.Exists(path))return null;using var directories=new UpdateDirectories();string actual=directories.Identity(path);
        knownDirectories??=original.Directories.Concat(CreatedDirectories()).GroupBy(record=>record.Path,StringComparer.OrdinalIgnoreCase).ToDictionary(group=>group.Key,group=>group.Select(record=>record.Identity).ToHashSet(),StringComparer.OrdinalIgnoreCase);
        return knownDirectories.TryGetValue(relative,out var ids) && ids.Contains(actual) ? actual : null;
    }
    internal bool CleanupCreatedDirectories()
    {
        using var directories=new UpdateDirectories();directories.Hold(root);
        UpdatePackage.Check(directories.Identity(root)==original.RootIdentity,"清理目标目录的原对象已被替换，记录已保留。");CleanupRootHeld?.Invoke();bool complete=true;
        foreach(var record in CreatedDirectories().OrderByDescending(record=>record.Path.Length))
        {
            string path=Path.Combine(root,record.Path);if(!QueryDirectory(path))continue;
            directories.Hold(path);if(directories.Identity(path)!=record.Identity)continue;
            directories.RemoveEmpty(path,record.Identity);
            if(QueryDirectory(path) && directories.Identity(path)==record.Identity)complete=false;
        }
        return complete;
    }
    private static bool QueryDirectory(string path)
    {
        try{return (File.GetAttributes(path)&FileAttributes.Directory)!=0;}
        catch(FileNotFoundException){return false;}
        catch(DirectoryNotFoundException){return false;}
    }
    internal void Replace(string key,FileStream input)
    {
        CheckRoot();string target=Target(key);using var directories=new UpdateDirectories();directories.Hold(Path.GetDirectoryName(target));
        var actual=Current(key);Evidence next;
        input.Position=0;UpdatePackage.Check(input.Length<=4194304,"新元数据超过预算。");next=new(key,input.Length,UpdatePackage.Hash(input,input.Length),UpdateFileIdentity.Read(input));
        string current=Path.Combine(directory,"metadata-"+key+".current.json"),pending=Path.Combine(directory,"metadata-"+key+".pending.json");
        // Keep current authority while a prospective replacement is durably recorded.
        if(actual.Identity!="")UpdateStorage.Atomic(current,JsonSerializer.SerializeToUtf8Bytes(actual,UpdateStorage.Json));
        UpdateStorage.Atomic(pending,JsonSerializer.SerializeToUtf8Bytes(next,UpdateStorage.Json));
        BeforeRename?.Invoke(input);
        if(actual.Identity!="")UpdatePackage.Check(UpdateFileIdentity.RemoveMatching(target,actual.Identity,new UpdateFile(Path.GetFileName(target),actual.Length,actual.Sha256)),"元数据被占用或替换，未覆盖。");
        UpdateFileIdentity.Rename(input,target);AfterCommit?.Invoke(key);File.Move(pending,current,true);
    }
}
