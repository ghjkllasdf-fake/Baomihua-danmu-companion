using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Popcorn;

internal sealed class UpdateApply
{
    internal string Root {get;}
    internal string Directory {get;}
    internal string Stage {get;}
    internal UpdatePackage Previous {get;}
    internal UpdatePackage Next {get;}
    internal bool Mutated {get;private set;}
    private readonly UpdateFile[] oldFiles,newFiles;
    private readonly Dictionary<string,UpdateFile> oldByPath,newByPath;
    private readonly Dictionary<string,string> originalIds;
    private readonly UpdateRepair repair;
    private readonly string ownershipIdentity;
    private sealed record Expected(UpdateFile File,string Identity,bool Incoming=false);
    private static UpdateOwnedFile[] Identities(string root,UpdateFile[] files)
    {
        var identities=new UpdateOwnedFile[files.Length];
        UpdatePackage.VerifyFiles(root,files,(index,held)=>identities[index]=new(files[index].Path,UpdateFileIdentity.Read(held)));return identities;
    }
    private UpdateApply(string root,string directory,string stage,UpdatePackage previous,UpdatePackage next,UpdateOwnedFile[] originals)
    {
        Root=root;Directory=directory;Stage=stage;Previous=previous;Next=next;oldFiles=previous?.Owned??Array.Empty<UpdateFile>();newFiles=next.Owned;
        oldByPath=oldFiles.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);newByPath=newFiles.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);
        originalIds=originals.ToDictionary(f=>f.Path,f=>f.Identity,StringComparer.OrdinalIgnoreCase);repair=new(root,directory,next.Version,next.Variant);
        string owned=UpdateInstalledOwnership.PathFor(root);ownershipIdentity=File.Exists(owned) ? UpdateFileIdentity.Read(owned) : "";
    }
    private static void Owns(string root,string directory)
    {
        UpdatePackage.Check(UpdateStorage.Owns(UpdateInstalledOwnership.ParentFor(root),directory),"覆盖更新记录不属于此安装目录。");
        using var owner=UpdateStorage.ReadJson(Path.Combine(directory,"owner.json"),4096);
        UpdatePackage.Check(owner.RootElement.GetProperty("purpose").GetString()=="direct-update","覆盖更新记录类型无效。");
    }
    private static byte[] Metadata(string directory,string name)
    {using var input=File.OpenRead(UpdatePackage.SafeFile(directory,name));return UpdatePackage.ReadLimited(input,4*1024*1024);}
    internal static UpdateApply Prepare(string root,string directory,string stage,string version,string variant,Action<string> trace=null)
    {
        root=UpdatePackage.SafeRoot(root);directory=UpdatePackage.SafeRoot(directory);stage=UpdatePackage.SafeRoot(stage);Owns(root,directory);
        var previous=UpdatePackage.Read(root);trace?.Invoke("read-old");UpdatePackage.Check(previous.Variant==variant,"原程序发行版不匹配。");var next=UpdatePackage.VerifyDirectory(stage,version,variant);trace?.Invoke("verify-stage");
        UpdatePackage.Check(UpdateVersion.Newer(version,previous.Version),"覆盖更新不允许降级或重复安装。");
        var identities=Identities(root,previous.Owned);var ids=identities.ToDictionary(f=>f.Path,f=>f.Identity,StringComparer.OrdinalIgnoreCase);var oldNames=ids.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var file in UpdateInstalledOwnership.Load(root).Where(f=>oldNames.Contains(f.Path)))
            UpdatePackage.Check(ids[file.Path]==file.Identity,"应用文件已被外来文件替换，未覆盖。");
        trace?.Invoke("identities");var apply=new UpdateApply(root,directory,stage,previous,next,identities);apply.Collisions();trace?.Invoke("preflight");UpdateInstalledOwnership.Probe(root,directory);
        // Metadata-only: never copy original executables, libraries, uninstaller or registry.
        UpdateStorage.Atomic(Path.Combine(directory,"previous-manifest.json"),previous.Manifest);UpdateStorage.Atomic(Path.Combine(directory,"next-manifest.json"),next.Manifest);
        UpdateStorage.Atomic(Path.Combine(directory,"original-identities.json"),JsonSerializer.SerializeToUtf8Bytes(identities,UpdateStorage.Json));apply.SetPhase("prepared");return apply;
    }
    internal static UpdateApply PrepareInstall(string root,string directory,string stage)
    {
        root=UpdatePackage.SafeRoot(root);stage=UpdatePackage.SafeRoot(stage);Owns(root,directory);var next=UpdatePackage.Read(stage);UpdatePackage.VerifyDirectory(stage,next.Version,next.Variant);
        UpdatePackage previous=null;
        if(System.IO.Directory.Exists(root) && System.IO.Directory.EnumerateFileSystemEntries(root).Any())
        {
            previous=UpdatePackage.Read(root);UpdatePackage.Check(previous.Variant==next.Variant,"原程序发行版不匹配。");
            UpdatePackage.Check(previous.Version==next.Version || UpdateVersion.Newer(next.Version,previous.Version),"不能用较旧的安装包覆盖当前版本。");
        }
        var files=previous?.Owned??Array.Empty<UpdateFile>();var identities=Identities(root,files);var ids=identities.ToDictionary(f=>f.Path,f=>f.Identity,StringComparer.OrdinalIgnoreCase);
        foreach(var file in UpdateInstalledOwnership.Load(root))
            UpdatePackage.Check(ids.TryGetValue(file.Path,out string id) && id==file.Identity,"原安装登记被外来文件替换，未覆盖。");
        var apply=new UpdateApply(root,directory,stage,previous,next,identities);apply.Collisions();UpdateInstalledOwnership.Probe(root,directory);
        if(previous!=null)UpdateStorage.Atomic(Path.Combine(directory,"previous-manifest.json"),previous.Manifest);
        UpdateStorage.Atomic(Path.Combine(directory,"next-manifest.json"),next.Manifest);UpdateStorage.Atomic(Path.Combine(directory,"original-identities.json"),JsonSerializer.SerializeToUtf8Bytes(identities,UpdateStorage.Json));
        apply.SetPhase("prepared");return apply;
    }
    internal static UpdateApply Load(string directory,string stageOverride=null)
    {
        directory=UpdatePackage.SafeRoot(directory);using var journal=UpdateStorage.ReadJson(Path.Combine(directory,"journal.json"),4096);var value=journal.RootElement;UpdatePackage.Unique(value);
        UpdatePackage.Check(value.GetProperty("schemaVersion").GetInt32()==2 && value.GetProperty("kind").GetString()=="direct-overwrite","不支持此覆盖更新记录，不能恢复旧版本。");
        string root=UpdatePackage.SafeRoot(value.GetProperty("targetRoot").GetString()),stage=UpdatePackage.SafeRoot(stageOverride??value.GetProperty("stage").GetString());Owns(root,directory);
        string previousVersion=value.GetProperty("previousVersion").GetString();
        var previous=previousVersion=="" ? null : UpdatePackage.Parse(Metadata(directory,"previous-manifest.json"),previousVersion,value.GetProperty("variant").GetString());
        var next=UpdatePackage.Parse(Metadata(directory,"next-manifest.json"),value.GetProperty("version").GetString(),value.GetProperty("variant").GetString());
        using var original=JsonDocument.Parse(Metadata(directory,"original-identities.json"));UpdatePackage.Unique(original.RootElement);
        var identities=original.RootElement.Deserialize<UpdateOwnedFile[]>(UpdateStorage.Json);var names=(previous?.Owned??Array.Empty<UpdateFile>()).Select(f=>f.Path).ToHashSet(StringComparer.Ordinal);
        UpdatePackage.Check(identities!=null && identities.Length==names.Count && identities.All(f=>f!=null && names.Contains(f.Path) && f.Identity!=null && f.Identity.Length==48 && f.Identity.All(char.IsAsciiHexDigit)) &&
            identities.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()==identities.Length && (previous==null || next.Version==previous.Version || UpdateVersion.Newer(next.Version,previous.Version)),"覆盖更新文件身份或版本记录无效。");
        return new(root,directory,stage,previous,next,identities);
    }
    internal void SetPhase(string phase)=>UpdateStorage.Atomic(Path.Combine(Directory,"journal.json"),JsonSerializer.SerializeToUtf8Bytes(new{
        schemaVersion=2,kind="direct-overwrite",targetRoot=Root,stage=Stage,previousVersion=Previous?.Version??"",version=Next.Version,variant=Next.Variant,phase}));
    private void VerifyStage()
    {
        Owns(Root,Directory);var staged=UpdatePackage.VerifyDirectory(Stage,Next.Version,Next.Variant);
        UpdatePackage.Check(staged.Manifest.AsSpan().SequenceEqual(Next.Manifest),"暂存清单已改变，未覆盖。");
    }
    internal void Preflight()
    {
        VerifyStage();
        UpdatePackage.VerifyFiles(Root,oldFiles,(index,held)=>UpdatePackage.Check(UpdateFileIdentity.Read(held)==originalIds[oldFiles[index].Path],"原程序路径被替换，未覆盖。"));
        Collisions();
    }
    private void Collisions()
    {
        using var directories=new UpdateDirectories();
        if(Previous==null)UpdatePackage.Check(!System.IO.Directory.Exists(Root) || !System.IO.Directory.EnumerateFileSystemEntries(Root).Any(),"新安装目录已有外来文件，未覆盖。");
        foreach(var file in newFiles.Where(f=>!oldByPath.ContainsKey(f.Path)))
        {string path=directories.File(Root,file.Path);UpdatePackage.Check(!File.Exists(path) && !System.IO.Directory.Exists(path),"更新与非应用文件或目录冲突，未覆盖。");}
    }
    private Dictionary<string,HashSet<string>> CreatedIds()
    {
        string[] paths=System.IO.Directory.GetFiles(Directory,"*.file-id");UpdatePackage.Check(paths.Length<=newFiles.Length*2,"覆盖文件记录过多。");
        var result=new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach(string path in paths)
        {
            using var input=File.OpenRead(UpdatePackage.SafeFile(Directory,Path.GetFileName(path)));string[] record=Encoding.UTF8.GetString(UpdatePackage.ReadLimited(input,2048)).Split('|');
            UpdatePackage.Check(record.Length==2 && record[1].Length==48 && record[1].All(char.IsAsciiHexDigit),"覆盖文件身份记录无效。");string relative=record[0].Replace('\\','/');
            string name=Path.GetFileName(path),number=name.EndsWith(".pending.file-id",StringComparison.Ordinal) ? name[..^16] : name[..^8];
            UpdatePackage.Check(int.TryParse(number,out int index) && index>0 && index<=newFiles.Length && newFiles[index-1].Path==relative &&
                (name==index+".file-id" || name==index+".pending.file-id"),"覆盖文件身份重复或越界。");
            if(!result.TryGetValue(relative,out var ids))result.Add(relative,ids=new(StringComparer.Ordinal));
            ids.Add(record[1]);
        }
        return result;
    }
    private Dictionary<string,Expected> RepairPreflight()
    {
        VerifyStage();using var directories=new UpdateDirectories();var created=CreatedIds();var result=new Dictionary<string,Expected>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in newFiles.Concat(oldFiles.Where(f=>!newByPath.ContainsKey(f.Path))))
        {
            string path=directories.File(Root,file.Path);UpdatePackage.Check(!System.IO.Directory.Exists(path),"修复路径被外来目录占用，未覆盖。");if(!File.Exists(path))continue;
            using var held=UpdateFileIdentity.OpenGuard(path);string identity=UpdateFileIdentity.Read(path);
            UpdateFile expected=null;bool incomingIdentity=false;
            if(oldByPath.TryGetValue(file.Path,out var old) && originalIds[file.Path]==identity)expected=old;
            else if(created.TryGetValue(file.Path,out var ids) && ids.Contains(identity) && newByPath.TryGetValue(file.Path,out var incoming)){expected=incoming;incomingIdentity=true;}
            if(expected==null && !newByPath.ContainsKey(file.Path))continue;
            if(expected!=null && !newByPath.ContainsKey(file.Path) && (held.Length!=expected.Length || !UpdatePackage.Hash(held,expected.Length).Equals(expected.Sha256,StringComparison.OrdinalIgnoreCase)))continue;
            held.Position=0;
            UpdatePackage.Check(expected!=null && UpdatePackage.Hash(held,expected.Length).Equals(expected.Sha256,StringComparison.OrdinalIgnoreCase),"修复发现外来或已改变的文件，未覆盖；请保留冲突文件后再修复。");
            result.Add(file.Path,new(expected,identity,incomingIdentity));
        }
        return result;
    }
    internal void Apply(Action<int> afterFile=null,Action<string> afterRemove=null)
    {
        Preflight();UpdateInstalledOwnership.Probe(Root,Directory);repair.Save();ApplyFiles(oldFiles.ToDictionary(f=>f.Path,f=>new Expected(f,originalIds[f.Path]),StringComparer.OrdinalIgnoreCase),afterFile,afterRemove);
    }
    internal void RepairCurrent(Action<string> afterProofPersist=null)
    {
        var expected=RepairPreflight();UpdateInstalledOwnership.Probe(Root,Directory);ApplyFiles(expected,null,null,afterProofPersist);
    }
    private void ApplyFiles(Dictionary<string,Expected> expected,Action<int> afterFile,Action<string> afterRemove,Action<string> afterProofPersist=null)
    {
        using var directories=new UpdateDirectories();directories.Hold(Root,true);directories.Hold(Directory);
        foreach(var file in oldFiles)
        {
            string parent=Path.GetDirectoryName(directories.File(Root,file.Path));if(System.IO.Directory.Exists(parent))directories.Hold(parent);
        }
        SetPhase("applying");int count=0;var owned=new Dictionary<string,UpdateOwnedFile>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in newFiles)
        {
            string target=directories.File(Root,file.Path,true),temporary=Path.Combine(Directory,"new-"+Guid.NewGuid().ToString("N")+".tmp");
            string canonical=Path.Combine(Directory,(++count)+".file-id"),pending=Path.Combine(Directory,count+".pending.file-id");
            if(expected.TryGetValue(file.Path,out var original) && !original.Incoming && original.File.Length==file.Length && original.File.Sha256.Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))
            {
                using var held=UpdateLauncher.GuardExecutable(target,file);string id=UpdateFileIdentity.Read(held);
                UpdatePackage.Check(id==original.Identity,"未改变的应用文件被外来文件替换，未重新登记。");
                // Preserve verified, unchanged objects; still record every incoming-owned path.
                UpdateStorage.Atomic(canonical,Encoding.UTF8.GetBytes(file.Path.Replace('/','\\')+"|"+id),directories);
                if(File.Exists(pending))File.Delete(pending);owned[file.Path]=new(file.Path,id);afterFile?.Invoke(count);continue;
            }
            try
            {
                using(var source=UpdateFileIdentity.OpenGuard(directories.File(Stage,file.Path)))using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){source.CopyTo(output);output.Flush(true);}
                UpdatePackage.VerifyFile(temporary,file);string identity=UpdateFileIdentity.Read(temporary);
                // Keep authority for the current object until its replacement is durably committed.
                if(expected.TryGetValue(file.Path,out var current) && current.Incoming)
                    UpdateStorage.Atomic(canonical,Encoding.UTF8.GetBytes(file.Path.Replace('/','\\')+"|"+current.Identity),directories);
                byte[] proof=Encoding.UTF8.GetBytes(file.Path.Replace('/','\\')+"|"+identity);UpdateStorage.Atomic(pending,proof,directories);
                afterProofPersist?.Invoke(file.Path);
                if(expected.TryGetValue(file.Path,out var old))
                {
                    UpdatePackage.Check(UpdateFileIdentity.RemoveMatching(target,old.Identity,old.File),"原文件被替换或占用，未覆盖该路径。请重新安装或保留冲突文件后修复。");Mutated=true;afterRemove?.Invoke(file.Path);
                }
                directories.Hold(Path.GetDirectoryName(target),true);File.Move(temporary,target,false);Mutated=true;
                File.Move(pending,canonical,true);owned[file.Path]=new(file.Path,identity);afterFile?.Invoke(count);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        foreach(var file in oldFiles.Where(f=>!newByPath.ContainsKey(f.Path)))
        {
            string target=directories.File(Root,file.Path);if(!expected.TryGetValue(file.Path,out var old) || !File.Exists(target))continue;
            UpdatePackage.Check(UpdateFileIdentity.RemoveMatching(target,old.Identity,old.File),"旧应用资源被占用或改变，请保留冲突文件后修复。");Mutated=true;
        }
        foreach(string parent in oldFiles.Select(file=>Path.GetDirectoryName(Path.Combine(Root,file.Path))).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(path=>path.Length))
            if(!parent.Equals(Root,StringComparison.OrdinalIgnoreCase))directories.RemoveEmpty(parent);
        UpdateInstalledOwnership.Save(Root,owned.Values.ToArray(),expectedIdentity:ownershipIdentity);AcceptInstalled();
    }
    internal void AcceptInstalled()
    {
        Owns(Root,Directory);UpdatePackage.Check(UpdatePackage.Read(Stage,Next.Version,Next.Variant).Manifest.AsSpan().SequenceEqual(Next.Manifest),"安装记录与可信暂存清单不匹配。");
        var created=CreatedIds();UpdatePackage.VerifyFiles(Root,newFiles,(index,held)=>UpdatePackage.Check(created.TryGetValue(newFiles[index].Path,out var ids) && ids.Contains(UpdateFileIdentity.Read(held)),"覆盖后的文件被替换，未确认成功。"));SetPhase("installed");
    }
    internal void CompleteMetadata(){SetPhase("complete");repair.Remove();}
}
