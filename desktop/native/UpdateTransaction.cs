using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Popcorn;

internal sealed class UpdateTransaction
{
    public string Root {get;}
    public string Directory {get;}
    public UpdatePackage Previous {get;}
    public UpdatePackage Next {get;private set;}
    private string Backup=>Path.Combine(Directory,"backup");
    private string Stage=>Path.Combine(Directory,"stage");
    private sealed record Created(string Path,string Identity);
    private Created[] created=Array.Empty<Created>();
    private UpdateTransaction(string root,string directory,UpdatePackage previous,UpdatePackage next)
    {Root=root;Directory=directory;Previous=previous;Next=next;}
    internal static string ParentFor(string root)=>Path.Combine(Path.GetDirectoryName(UpdatePackage.SafeRoot(root)),".DanmuCompanion-updates");
    private static void Owned(string root,string directory)
    {
        UpdatePackage.Check(UpdateStorage.Owns(ParentFor(root),directory),"更新事务目录不属于本应用的安装位置。");
        using var input=File.OpenRead(Path.Combine(directory,"owner.json"));using var doc=JsonDocument.Parse(UpdatePackage.ReadLimited(input,4096));
        UpdatePackage.Check(doc.RootElement.GetProperty("purpose").GetString()=="transaction","更新事务所属类型不匹配。");
    }
    public static UpdateTransaction Prepare(string root,string directory,string version,string variant)
    {
        root=UpdatePackage.SafeRoot(root);directory=UpdatePackage.SafeRoot(directory);Owned(root,directory);
        var previous=UpdatePackage.Read(root);UpdatePackage.VerifyDirectory(root,previous.Version,variant);
        var next=UpdatePackage.VerifyDirectory(Path.Combine(directory,"stage"),version,variant);
        UpdatePackage.Check(UpdateVersion.Newer(next.Version,previous.Version),"更新事务不允许降级或重新安装同一版本。");
        var names=previous.Owned.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var file in next.Owned)
        {
            string path=UpdatePackage.SafeFile(root,file.Path);
            UpdatePackage.Check(!File.Exists(path) || names.Contains(file.Path),"新版本与非本应用文件冲突，未覆盖。");
            UpdatePackage.Check(!System.IO.Directory.Exists(path),"新版本与现有目录冲突，未覆盖。");
        }
        UpdatePackage.Check(new DriveInfo(Path.GetPathRoot(directory)).AvailableFreeSpace>previous.Bytes+32L*1024*1024,"磁盘空间不足，未开始替换应用。");
        string backup=Path.Combine(directory,"backup");UpdatePackage.Check(!System.IO.Directory.Exists(backup),"更新备份已存在，未覆盖。");System.IO.Directory.CreateDirectory(backup);
        foreach(var file in previous.Owned)
        {
            string target=UpdatePackage.SafeFile(backup,file.Path);System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(UpdatePackage.SafeFile(root,file.Path),target);UpdatePackage.VerifyFile(target,file);
        }
        UpdateStorage.Atomic(Path.Combine(directory,"next-manifest.json"),next.Manifest);
        var transaction=new UpdateTransaction(root,directory,previous,next);transaction.SetPhase("prepared");return transaction;
    }
    public static UpdateTransaction Load(string directory)
    {
        directory=UpdatePackage.SafeRoot(directory);using var input=File.OpenRead(Path.Combine(directory,"journal.json"));
        using var doc=JsonDocument.Parse(UpdatePackage.ReadLimited(input,4096));var journal=doc.RootElement;UpdatePackage.Unique(journal);
        UpdatePackage.Check(journal.GetProperty("schemaVersion").GetInt32()==1,"不支持的更新恢复记录。");
        string root=UpdatePackage.SafeRoot(journal.GetProperty("targetRoot").GetString()),version=journal.GetProperty("version").GetString(),variant=journal.GetProperty("variant").GetString();Owned(root,directory);
        var old=UpdatePackage.Read(Path.Combine(directory,"backup"));UpdatePackage.VerifyDirectory(Path.Combine(directory,"backup"),old.Version,variant);
        using var nextInput=File.OpenRead(Path.Combine(directory,"next-manifest.json"));var next=UpdatePackage.Parse(UpdatePackage.ReadLimited(nextInput,4*1024*1024),version,variant);
        UpdatePackage.Check(UpdateVersion.Newer(next.Version,old.Version),"更新恢复版本关系无效。");
        var transaction=new UpdateTransaction(root,directory,old,next);
        string createdPath=Path.Combine(directory,"created-files.json");
        if(File.Exists(createdPath))
        {
            using var records=File.OpenRead(createdPath);transaction.created=JsonSerializer.Deserialize<Created[]>(UpdatePackage.ReadLimited(records,4*1024*1024),UpdateStorage.Json);
            UpdatePackage.Check(transaction.created!=null && transaction.created.Length<=next.Owned.Length &&
                transaction.created.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()==transaction.created.Length &&
                transaction.created.All(f=>f.Identity!=null && f.Identity.Length==48 && f.Identity.All(char.IsAsciiHexDigit) &&
                    next.Owned.Any(n=>n.Path==f.Path) && !old.Owned.Any(n=>n.Path.Equals(f.Path,StringComparison.OrdinalIgnoreCase))),"新文件恢复身份记录无效。");
        }
        return transaction;
    }
    public void SetPhase(string phase)=>UpdateStorage.Atomic(Path.Combine(Directory,"journal.json"),JsonSerializer.SerializeToUtf8Bytes(new{
        schemaVersion=1,targetRoot=Root,version=Next.Version,variant=Next.Variant,phase}));
    private static void Replace(string source,string target,bool overwrite=true,Action<string> beforeMove=null)
    {
        UpdatePackage.SafeRoot(Path.GetDirectoryName(source));UpdatePackage.SafeRoot(Path.GetDirectoryName(target));System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target));
        string temporary=target+"."+Guid.NewGuid().ToString("N")+".update.tmp";
        try
        {
            File.Copy(source,temporary);
            beforeMove?.Invoke(temporary);
            for(int attempt=0;;attempt++)
            {
                try{File.Move(temporary,target,overwrite);break;}
                catch(IOException error) when((error.HResult&0xffff) is 32 or 33 && attempt<20){Thread.Sleep(250);}
            }
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public void Apply(Action<int> afterFile=null)
    {
        Owned(Root,Directory);UpdatePackage.VerifyDirectory(Root,Previous.Version,Previous.Variant);UpdatePackage.VerifyDirectory(Stage,Next.Version,Next.Variant);SetPhase("applying");
        try
        {
            int count=0;
            var oldNames=Previous.Owned.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach(var file in Next.Owned)
            {
                string target=UpdatePackage.SafeFile(Root,file.Path);bool overwrite=oldNames.Contains(file.Path);
                if(overwrite)UpdatePackage.VerifyFile(target,Previous.Owned.First(f=>f.Path.Equals(file.Path,StringComparison.OrdinalIgnoreCase)));
                else UpdatePackage.Check(!File.Exists(target) && !System.IO.Directory.Exists(target),"新版本与刚出现的非应用文件冲突，未覆盖。");
                Replace(UpdatePackage.SafeFile(Stage,file.Path),target,overwrite,overwrite ? null : temporary=>RecordCreated(file.Path,temporary));
                afterFile?.Invoke(++count);
            }
            var retained=Next.Owned.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach(var file in Previous.Owned.Where(f=>!retained.Contains(f.Path)))
            {string path=UpdatePackage.SafeFile(Root,file.Path);UpdatePackage.VerifyFile(path,file);File.Delete(path);}
            UpdatePackage.VerifyDirectory(Root,Next.Version,Next.Variant);SetPhase("installed");
        }
        catch
        {
            try{Restore();}catch{SetPhase("rollback-failed");throw new InvalidDataException("更新替换与恢复均失败，备份已保留，请运行更新目录中的恢复组件。");}throw;
        }
    }
    internal void RecordCreated(string path,string temporary)
    {
        created=created.Where(f=>!f.Path.Equals(path,StringComparison.OrdinalIgnoreCase)).Append(new Created(path,UpdateFileIdentity.Read(temporary))).ToArray();
        // Record the temporary file's disk identity before its non-overwriting rename.
        UpdateStorage.Atomic(Path.Combine(Directory,"created-files.json"),JsonSerializer.SerializeToUtf8Bytes(created,UpdateStorage.Json));
    }
    public void Restore()
    {
        Owned(Root,Directory);UpdatePackage.VerifyDirectory(Backup,Previous.Version,Previous.Variant);
        var oldNames=Previous.Owned.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Matches(string path,UpdateFile file){try{UpdatePackage.VerifyFile(path,file);return true;}catch(InvalidDataException){return false;}}
        foreach(var file in Previous.Owned)
        {
            string target=UpdatePackage.SafeFile(Root,file.Path);if(!File.Exists(target))continue;
            var replacement=Next.Owned.FirstOrDefault(f=>f.Path.Equals(file.Path,StringComparison.OrdinalIgnoreCase));
            UpdatePackage.Check(Matches(target,file) || replacement!=null && Matches(target,replacement),"恢复时发现文件已被其他程序更改，备份已保留。");
        }
        foreach(var file in Next.Owned.Where(f=>!oldNames.Contains(f.Path)))
        {
            string target=UpdatePackage.SafeFile(Root,file.Path);if(!File.Exists(target))continue;
            var record=created.FirstOrDefault(f=>f.Path.Equals(file.Path,StringComparison.OrdinalIgnoreCase));
            if(record==null || UpdateFileIdentity.Read(target)!=record.Identity)continue;
            UpdatePackage.VerifyFile(target,file);File.Delete(target);
        }
        foreach(var file in Previous.Owned)
        {
            string target=UpdatePackage.SafeFile(Root,file.Path);if(File.Exists(target) && Matches(target,file))continue;
            Replace(UpdatePackage.SafeFile(Backup,file.Path),target);
        }
        UpdatePackage.VerifyDirectory(Root,Previous.Version,Previous.Variant);SetPhase("rolledback");
    }
}
