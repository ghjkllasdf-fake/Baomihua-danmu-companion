using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace Popcorn;

internal interface IUpdateRegistration
{
    void Save(string directory);
    void Restore();
}

internal sealed class UpdateRegistration : IUpdateRegistration
{
    private sealed record Value(string Name,RegistryValueKind Kind,JsonElement Data);
    private readonly string root,variant;
    private readonly Value[] values;
    private UpdateRegistration(string root,string variant,Value[] values){this.root=root;this.variant=variant;this.values=values;}
    private static bool SameRoot(string location,string root)=>location!=null && Path.IsPathFullyQualified(location) &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)),root,StringComparison.OrdinalIgnoreCase);
    private static bool Matches(RegistryKey key,string root)=>key.GetValue("InstallLocation") is string location && Path.IsPathFullyQualified(location) &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)),root,StringComparison.OrdinalIgnoreCase);
    private static void ValidateValues(Value[] values,string root)
    {
        UpdatePackage.Check(values!=null && values.Length<=64 && values.All(v=>v!=null && v.Name!=null && v.Name.Length<=256 && !v.Name.Any(char.IsControl)) &&
            values.Select(v=>v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()==values.Length,"安装恢复值无效。");
        foreach(var value in values)
        {
            UpdatePackage.Check(value.Data.GetRawText().Length<=16384,"安装登记值超过恢复预算。");
            bool valid=value.Kind switch
            {
                RegistryValueKind.String or RegistryValueKind.ExpandString=>value.Data.ValueKind==JsonValueKind.String,
                RegistryValueKind.DWord=>value.Data.TryGetInt32(out _),RegistryValueKind.QWord=>value.Data.TryGetInt64(out _),
                RegistryValueKind.Binary=>value.Data.ValueKind==JsonValueKind.String && value.Data.TryGetBytesFromBase64(out _),
                RegistryValueKind.MultiString=>value.Data.ValueKind==JsonValueKind.Array && value.Data.EnumerateArray().All(v=>v.ValueKind==JsonValueKind.String),_=>false
            };
            UpdatePackage.Check(valid,"安装登记包含无效或不支持的恢复值。");
        }
        UpdatePackage.Check(values.Any(v=>v.Name.Equals("InstallLocation",StringComparison.OrdinalIgnoreCase) && v.Kind==RegistryValueKind.String && SameRoot(v.Data.GetString(),root)),"安装恢复记录缺少所属证明。");
    }
    public static UpdateRegistration Capture(string root,string variant)
    {
        root=UpdatePackage.SafeRoot(root);using var registry=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);
        using var key=registry.OpenSubKey(UpdateIdentity.UninstallKey(variant));UpdatePackage.Check(key!=null && Matches(key,root) && key.GetSubKeyNames().Length==0,"原安装登记的所属证明无效。");
        string[] names=key.GetValueNames();UpdatePackage.Check(names.Length<=64,"安装登记超过更新备份范围。");
        var values=names.Select(name=>new Value(name,key.GetValueKind(name),JsonSerializer.SerializeToElement(key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames)))).ToArray();
        ValidateValues(values,root);
        return new(root,variant,values);
    }
    public void Save(string directory)=>UpdateStorage.Atomic(Path.Combine(directory,"registration.json"),JsonSerializer.SerializeToUtf8Bytes(new{root,variant,values},UpdateStorage.Json));
    public static UpdateRegistration Load(string directory,string expectedRoot,string expectedVariant)
    {
        using var stream=File.OpenRead(Path.Combine(directory,"registration.json"));using var doc=JsonDocument.Parse(UpdatePackage.ReadLimited(stream,256*1024));var saved=doc.RootElement;UpdatePackage.Unique(saved);
        UpdatePackage.Check(SameRoot(saved.GetProperty("root").GetString(),expectedRoot) && saved.GetProperty("variant").GetString()==expectedVariant,"安装恢复记录与当前应用不匹配。");
        var values=saved.GetProperty("values").Deserialize<Value[]>(UpdateStorage.Json);
        ValidateValues(values,expectedRoot);
        return new(expectedRoot,expectedVariant,values);
    }
    public void Restore()
    {
        using var registry=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);using var existing=registry.OpenSubKey(UpdateIdentity.UninstallKey(variant));
        UpdatePackage.Check(existing==null || Matches(existing,root),"安装登记已指向其他目录，未覆盖。");
        using var key=registry.CreateSubKey(UpdateIdentity.UninstallKey(variant));UpdatePackage.Check(key.GetSubKeyNames().Length==0,"安装登记结构已改变，未覆盖。");
        foreach(string name in key.GetValueNames().Where(n=>!values.Any(v=>v.Name.Equals(n,StringComparison.OrdinalIgnoreCase))))key.DeleteValue(name);
        foreach(var value in values)
        {
            object data=value.Kind switch
            {
                RegistryValueKind.String or RegistryValueKind.ExpandString=>value.Data.GetString(),RegistryValueKind.DWord=>value.Data.GetInt32(),RegistryValueKind.QWord=>value.Data.GetInt64(),
                RegistryValueKind.Binary=>value.Data.GetBytesFromBase64(),RegistryValueKind.MultiString=>value.Data.Deserialize<string[]>(),_=>throw new InvalidDataException("不支持的安装恢复值。")
            };
            key.SetValue(value.Name,data,value.Kind);
        }
    }
}

internal sealed class UpdateInstallerTransaction
{
    public string Root {get;}
    public string Directory {get;}
    public string Version {get;}
    public string Variant {get;}
    private readonly UpdatePackage previous;
    private readonly UpdateFile[] originals;
    private readonly IUpdateRegistration registration;
    private UpdatePackage next;
    private UpdateOwnedFile[] nativeOwned=Array.Empty<UpdateOwnedFile>();
    internal string RecoveryNotice {get;private set;}="";
    private string Backup=>Path.Combine(Directory,"backup");
    private UpdateInstallerTransaction(string root,string directory,string version,string variant,UpdatePackage previous,UpdateFile[] originals,IUpdateRegistration registration)
    {Root=root;Directory=directory;Version=version;Variant=variant;this.previous=previous;this.originals=originals;this.registration=registration;}
    public static UpdateInstallerTransaction Prepare(string root,string directory,string version,string variant,IUpdateRegistration registration,UpdatePackage incoming)
    {
        root=UpdatePackage.SafeRoot(root);UpdatePackage.Check(UpdateStorage.Owns(UpdateTransaction.ParentFor(root),directory),"安装更新事务的所属证明无效。");
        var previous=UpdatePackage.Read(root);UpdatePackage.VerifyDirectory(root,previous.Version,variant);UpdatePackage.Check(UpdateVersion.Newer(version,previous.Version),"安装更新不允许降级。");
        var nativeOwned=UpdateInstalledOwnership.Load(root).Where(f=>previous.Owned.Any(p=>p.Path.Equals(f.Path,StringComparison.OrdinalIgnoreCase))).ToArray();
        foreach(var file in nativeOwned)UpdatePackage.Check(UpdateFileIdentity.Read(UpdatePackage.SafeFile(root,file.Path))==file.Identity,"此前新增的应用文件已被外来文件替换，未覆盖。");
        UpdatePackage.Check(incoming!=null && incoming.Version==version && incoming.Variant==variant,"安装更新缺少经过校验的目标文件清单。");
        var oldNames=previous.Owned.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var file in incoming.Owned.Where(f=>!oldNames.Contains(f.Path)))
        {string path=UpdatePackage.SafeFile(root,file.Path);UpdatePackage.Check(!File.Exists(path) && !System.IO.Directory.Exists(path),"安装更新与非应用文件冲突，未退出原程序。");}
        var extras=new[]{"unins000.exe","unins000.dat","unins000.msg"}.Where(name=>File.Exists(UpdatePackage.SafeFile(root,name))).Select(name=>
        {using var stream=File.OpenRead(UpdatePackage.SafeFile(root,name));UpdatePackage.Check(stream.Length<=16*1024*1024,"卸载组件超过备份预算。");return new UpdateFile(name,stream.Length,UpdatePackage.Hash(stream,stream.Length));});
        var files=previous.Owned.Concat(extras).ToArray();string backup=Path.Combine(directory,"backup");UpdatePackage.Check(!System.IO.Directory.Exists(backup),"安装更新备份已存在。");System.IO.Directory.CreateDirectory(backup);
        foreach(var file in files)
        {string target=UpdatePackage.SafeFile(backup,file.Path);System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(UpdatePackage.SafeFile(root,file.Path),target);UpdatePackage.VerifyFile(target,file);}
        registration.Save(directory);UpdateStorage.Atomic(Path.Combine(directory,"original-files.json"),JsonSerializer.SerializeToUtf8Bytes(files,UpdateStorage.Json));
        UpdateStorage.Atomic(Path.Combine(directory,"installer-originals.txt"),System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n",files.Where(f=>!nativeOwned.Any(n=>n.Path.Equals(f.Path,StringComparison.OrdinalIgnoreCase))).Select(f=>f.Path.Replace('/','\\')))+"\r\n"));
        UpdateInstalledOwnership.Save(root,nativeOwned,Path.Combine(directory,"ownership-before.txt"));
        UpdateStorage.Atomic(Path.Combine(directory,"next-manifest.json"),incoming.Manifest);
        var transaction=new UpdateInstallerTransaction(root,directory,version,variant,previous,files,registration){next=incoming,nativeOwned=nativeOwned};transaction.SetPhase("prepared");return transaction;
    }
    public static UpdateInstallerTransaction Load(string directory,IUpdateRegistration registration=null)
    {
        using var input=File.OpenRead(Path.Combine(directory,"journal.json"));using var doc=JsonDocument.Parse(UpdatePackage.ReadLimited(input,4096));var journal=doc.RootElement;UpdatePackage.Unique(journal);
        string root=UpdatePackage.SafeRoot(journal.GetProperty("targetRoot").GetString()),version=journal.GetProperty("version").GetString(),variant=journal.GetProperty("variant").GetString();
        UpdatePackage.Check(UpdateStorage.Owns(UpdateTransaction.ParentFor(root),directory) && journal.GetProperty("schemaVersion").GetInt32()==1 && journal.GetProperty("kind").GetString()=="installer","安装恢复事务不匹配。");
        var previous=UpdatePackage.Read(Path.Combine(directory,"backup"));UpdatePackage.VerifyDirectory(Path.Combine(directory,"backup"),previous.Version,variant);
        using var files=File.OpenRead(Path.Combine(directory,"original-files.json"));var originals=JsonSerializer.Deserialize<UpdateFile[]>(UpdatePackage.ReadLimited(files,4*1024*1024),UpdateStorage.Json);
        UpdatePackage.Check(UpdateVersion.Newer(version,previous.Version) && originals!=null && originals.Length<=previous.Files.Length+4 &&
            originals.All(f=>f!=null) && originals.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()==originals.Length &&
            previous.Owned.All(o=>originals.Contains(o)) && originals.All(f=>previous.Owned.Any(o=>o==f) || new[]{"unins000.exe","unins000.dat","unins000.msg"}.Contains(f.Path) && f.Length>=0 && f.Length<=16*1024*1024 && UpdateCatalog.HashValid(f.Sha256)),"安装恢复文件的所属证明无效。");
        foreach(var file in originals)UpdatePackage.VerifyFile(UpdatePackage.SafeFile(Path.Combine(directory,"backup"),file.Path),file);
        registration??=UpdateRegistration.Load(directory,root,variant);var transaction=new UpdateInstallerTransaction(root,directory,version,variant,previous,originals,registration);
        using var next=File.OpenRead(Path.Combine(directory,"next-manifest.json"));transaction.next=UpdatePackage.Parse(UpdatePackage.ReadLimited(next,4*1024*1024),version,variant);
        transaction.nativeOwned=UpdateInstalledOwnership.Load(root,Path.Combine(directory,"ownership-before.txt"));
        UpdatePackage.Check(transaction.nativeOwned.All(f=>previous.Owned.Any(p=>p.Path.Equals(f.Path,StringComparison.OrdinalIgnoreCase))),"新增文件恢复登记越过原应用清单。");return transaction;
    }
    public void SetPhase(string phase)=>UpdateStorage.Atomic(Path.Combine(Directory,"journal.json"),JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=1,kind="installer",targetRoot=Root,version=Version,variant=Variant,phase}));
    internal void PreflightApply()
    {
        UpdatePackage.VerifyDirectory(Root,previous.Version,Variant);
        foreach(var file in originals)UpdatePackage.VerifyFile(UpdatePackage.SafeFile(Root,file.Path),file);
        var names=originals.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var file in nativeOwned)UpdatePackage.Check(UpdateFileIdentity.Read(UpdatePackage.SafeFile(Root,file.Path))==file.Identity,"此前新增的应用文件已被替换，未覆盖。");
        foreach(var file in next.Owned.Where(f=>!names.Contains(f.Path)))
        {string target=UpdatePackage.SafeFile(Root,file.Path);UpdatePackage.Check(!File.Exists(target) && !System.IO.Directory.Exists(target),"新版本与刚出现的外来文件冲突，未覆盖。");}
    }
    internal void CreateNewFiles()
    {
        var stage=UpdatePackage.VerifyDirectory(Path.Combine(Directory,"stage"),Version,Variant);
        UpdatePackage.Check(stage.Manifest.AsSpan().SequenceEqual(next.Manifest),"新文件暂存清单与校验目标不一致。");
        var names=originals.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);int count=0;
        foreach(var file in next.Owned.Where(f=>!names.Contains(f.Path) || nativeOwned.Any(n=>n.Path.Equals(f.Path,StringComparison.OrdinalIgnoreCase))))
        {
            string target=UpdatePackage.SafeFile(Root,file.Path),temporary=Path.Combine(Directory,"new-"+Guid.NewGuid().ToString("N")+".tmp");
            try
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(UpdatePackage.SafeFile(Path.Combine(Directory,"stage"),file.Path),temporary);UpdatePackage.VerifyFile(temporary,file);
                UpdateStorage.Atomic(Path.Combine(Directory,(++count)+".file-id"),System.Text.Encoding.UTF8.GetBytes(file.Path.Replace('/','\\')+"|"+UpdateFileIdentity.Read(temporary)));
                var owned=nativeOwned.FirstOrDefault(n=>n.Path.Equals(file.Path,StringComparison.OrdinalIgnoreCase));
                if(owned!=null)UpdatePackage.Check(UpdateFileIdentity.RemoveMatching(target,owned.Identity),"此前新增文件已被替换或占用，未覆盖。");
                File.Move(temporary,target,false);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
    public void AcceptInstalled()
    {
        var actual=UpdatePackage.VerifyDirectory(Root,Version,Variant);
        UpdatePackage.Check(actual.Manifest.AsSpan().SequenceEqual(next.Manifest),"安装结果与 GitHub 校验过的文件清单不同，未确认更新成功。");
        var proofs=ReadCreated();
        foreach(var proof in proofs)UpdatePackage.Check(UpdateFileIdentity.Read(UpdatePackage.SafeFile(Root,proof.Path))==proof.Identity,"安装新增文件已被外来文件替换，未认领或确认成功。");
        var retained=next.Owned.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var file in previous.Owned.Where(f=>!retained.Contains(f.Path)))
        {
            string path=UpdatePackage.SafeFile(Root,file.Path);if(!File.Exists(path))continue;
            var owned=nativeOwned.FirstOrDefault(n=>n.Path.Equals(file.Path,StringComparison.OrdinalIgnoreCase));
            if(owned!=null)
            {
                if(UpdateFileIdentity.Read(path)!=owned.Identity)RecoveryNotice="更新已完成；已保留替换了旧应用路径的外来文件。";
                else UpdatePackage.Check(UpdateFileIdentity.RemoveMatching(path,owned.Identity),"旧应用文件仍被占用或已改变，备份和恢复登记已保留。");
            }
            else {UpdatePackage.VerifyFile(path,file);File.Delete(path);}
        }
        SetPhase("installed");
        var managed=nativeOwned.Where(f=>retained.Contains(f.Path)).Select(f=>proofs.FirstOrDefault(p=>p.Path.Equals(f.Path,StringComparison.OrdinalIgnoreCase)) ?? new Created(f.Path,f.Identity)).Concat(proofs)
            .DistinctBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).ToArray();
        UpdateInstalledOwnership.Save(Root,managed.Select(f=>new UpdateOwnedFile(f.Path,f.Identity)).ToArray());
    }
    public void Restore()
    {
        UpdatePackage.Check(UpdateStorage.Owns(UpdateTransaction.ParentFor(Root),Directory),"安装恢复目录的所属证明无效。");
        UpdatePackage current=null;
        // Unreadable metadata is not evidence of an interrupted install: locks and access failures must stop before any write.
        try{current=UpdatePackage.Read(Root);}
        catch(Exception error) when(error is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or JsonException or
            InvalidOperationException or ArgumentException or System.Collections.Generic.KeyNotFoundException or FormatException or OverflowException){}
        UpdatePackage.Check(current==null || current.Variant==Variant && (current.Version==previous.Version || current.Version==Version),"安装目录已是其他版本或发行版，旧恢复事务不会覆盖它。");
        foreach(var file in originals)UpdatePackage.VerifyFile(UpdatePackage.SafeFile(Backup,file.Path),file);
        Created[] created=Array.Empty<Created>();
        var restoredIdentities=nativeOwned.ToDictionary(f=>f.Path,f=>f.Identity,StringComparer.OrdinalIgnoreCase);
        try{created=ReadCreated();}catch(Exception error) when(UpdateStorage.MetadataError(error)){RecoveryNotice="原文件和安装登记已恢复；新增文件记录不完整，无法证明所属的文件已保留，请检查更新目录。";}
        foreach(var file in originals)
        {
            string target=UpdatePackage.SafeFile(Root,file.Path);System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target));
            var owned=nativeOwned.FirstOrDefault(n=>n.Path.Equals(file.Path,StringComparison.OrdinalIgnoreCase));
            if(owned!=null && File.Exists(target))
            {
                string identity=UpdateFileIdentity.Read(target);
                if(identity!=owned.Identity && !created.Any(c=>c.Path.Equals(file.Path,StringComparison.OrdinalIgnoreCase) && c.Identity==identity))
                {RecoveryNotice="外来文件替换了此前新增的应用路径，已保留；请检查更新备份后手动恢复。";continue;}
                UpdatePackage.Check(UpdateFileIdentity.RemoveMatching(target,identity),"恢复前文件已改变，未覆盖。");
            }
            string temp=target+"."+Guid.NewGuid().ToString("N")+".update.tmp";
            try
            {
                File.Copy(UpdatePackage.SafeFile(Backup,file.Path),temp);string restoredIdentity=owned==null ? "" : UpdateFileIdentity.Read(temp);
                File.Move(temp,target,owned==null);if(owned!=null)restoredIdentities[file.Path]=restoredIdentity;
            }
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        registration.Restore();UpdatePackage.VerifyDirectory(Root,previous.Version,Variant);
        foreach(var file in created)
        {
            string target=UpdatePackage.SafeFile(Root,file.Path);if(!File.Exists(target))continue;
            if(UpdateFileIdentity.Read(target)!=file.Identity)continue;
            UpdatePackage.Check(UpdateFileIdentity.RemoveMatching(target,file.Identity),"新增更新文件仍被占用或已改变，备份和恢复登记已保留。");
        }
        UpdateInstalledOwnership.Save(Root,restoredIdentities.Select(f=>new UpdateOwnedFile(f.Key,f.Value)).ToArray());SetPhase("rolledback");
    }
    private sealed record Created(string Path,string Identity);
    private Created[] ReadCreated()
    {
        string[] records=System.IO.Directory.GetFiles(Directory,"*.file-id");UpdatePackage.Check(records.Length<=next.Owned.Length,"安装新增文件记录过多。");
        var names=originals.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files=records.Select(path=>
        {
            UpdatePackage.SafeFile(Directory,Path.GetFileName(path));using var input=File.OpenRead(path);
            string[] value=System.Text.Encoding.UTF8.GetString(UpdatePackage.ReadLimited(input,2048)).TrimStart('\ufeff').Split('|');
            UpdatePackage.Check(value.Length==2,"安装新增文件记录无效。");string relative=value[0].Replace('\\','/');UpdatePackage.SafeRelative(relative);
            UpdatePackage.Check((!names.Contains(relative) || nativeOwned.Any(f=>f.Path.Equals(relative,StringComparison.OrdinalIgnoreCase))) && next.Owned.Any(f=>f.Path.Equals(relative,StringComparison.OrdinalIgnoreCase)) && value[1].Length==48 && value[1].All(char.IsAsciiHexDigit),"安装新增文件记录越过目标清单。");return new Created(relative,value[1]);
        }).ToArray();UpdatePackage.Check(files.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()==files.Length,"安装新增文件记录重复。");return files;
    }
    internal void RecordCreatedForTest(string relative)
    {
        UpdateStorage.Atomic(Path.Combine(Directory,(System.IO.Directory.GetFiles(Directory,"*.file-id").Length+1)+".file-id"),System.Text.Encoding.UTF8.GetBytes(relative+"|"+UpdateFileIdentity.Read(UpdatePackage.SafeFile(Root,relative))));
    }
}
