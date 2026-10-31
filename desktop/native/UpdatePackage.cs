using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Runtime.ExceptionServices;

namespace Popcorn;

internal sealed record UpdateFile(string Path,long Length,string Sha256);

internal sealed class UpdatePackage
{
    public string Version {get;}
    public string Variant {get;}
    public UpdateFile[] Files {get;}
    public byte[] Manifest {get;}
    public long Bytes=>Files.Sum(f=>f.Length)+Manifest.Length;
    public UpdateFile[] Owned=>Files.Append(new UpdateFile("manifest.json",Manifest.Length,Convert.ToHexString(SHA256.HashData(Manifest)).ToLowerInvariant())).ToArray();
    private UpdatePackage(string version,string variant,UpdateFile[] files,byte[] raw){Version=version;Variant=variant;Files=files;Manifest=raw;}
    internal static void Check(bool valid,string message){if(!valid)throw new InvalidDataException(message);}
    internal static void Unique(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var properties=element.EnumerateObject().ToArray();Check(properties.Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==properties.Length,"JSON 包含重复属性。");
            foreach(var property in properties)Unique(property.Value);
        }
        else if(element.ValueKind==JsonValueKind.Array)foreach(var child in element.EnumerateArray())Unique(child);
    }
    public static void SafeRelative(string path)
    {
        Check(path!=null && path.Length is >0 and <512 && !path.StartsWith('/') && !path.Contains('\\') && !path.Any(char.IsControl) && path.IndexOfAny(":<>\"|?*".ToCharArray())<0,"更新包路径无效。");
        Check(path.Split('/').All(p=>p.Length>0 && p is not "." and not ".." && !p.EndsWith('.') && !p.EndsWith(' ') &&
            !Regex.IsMatch(p,@"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)",RegexOptions.IgnoreCase)),"更新包包含不安全的 Windows 路径。");
    }
    private static bool Allowed(string path,bool includeApi)=>
        Regex.IsMatch(path,@"\A(?:DanmuCompanion(?:\.Probe|\.Updater)?\.exe|DanmuCompanion(?:\.Probe|\.Updater)?\.(?:deps|runtimeconfig)\.json|MicrosoftEdgeWebview2Setup\.exe|createdump\.exe|README\.md|LICENSE|DISTRIBUTION-LICENSE\.txt|THIRD-PARTY-NOTICES\.md|install-files\.txt)\z") ||
        Regex.IsMatch(path,@"\A(?:[a-z]{2}(?:-[A-Za-z0-9]+)?/)?[A-Za-z0-9_.-]+\.dll\z") ||
        Regex.IsMatch(path,@"\Aassets/(?:app|tray)\.ico\z") ||
        Regex.IsMatch(path,@"\Aui/dist/(?:index\.html|favicon\.ico|assets/[A-Za-z0-9_.-]+\.(?:js|css|svg|png|jpg|webp|woff2))\z") ||
        Regex.IsMatch(path,@"\Alicenses/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\.(?:txt|rtf|json|nuspec|html|md)\z") ||
        includeApi && path.StartsWith("api/",StringComparison.Ordinal);
    public static UpdatePackage Parse(byte[] raw,string expectedVersion,string expectedVariant,Action<string> trace=null)
    {
        Check(raw.Length is >0 and <=4*1024*1024,"更新包清单过大。");
        using var doc=JsonDocument.Parse(raw,new JsonDocumentOptions{MaxDepth=16});var root=doc.RootElement;Unique(root);trace?.Invoke("json");
        string version=root.GetProperty("version").GetString(),variant=root.GetProperty("variant").GetString();
        Check(root.GetProperty("schemaVersion").GetInt32()==1 && version==expectedVersion && UpdateVersion.TryParse(version,out _) &&
            variant==expectedVariant && variant is "standard" or "includeAPI" && !root.GetProperty("sourceDirty").GetBoolean() &&
            Regex.IsMatch(root.GetProperty("sourceCommit").GetString()??"",@"\A[a-f0-9]{40}\z"),"更新包版本、来源或发行版不匹配。");
        var array=root.GetProperty("files");Check(array.ValueKind==JsonValueKind.Array && array.GetArrayLength() is >0 and <=16384,"更新包文件数量无效。");
        var files=array.EnumerateArray().Select(file=>new UpdateFile(file.GetProperty("path").GetString(),file.GetProperty("length").GetInt64(),file.GetProperty("sha256").GetString())).ToArray();
        trace?.Invoke("entries");
        Check(files.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()==files.Length,"更新包包含重名文件。");
        foreach(var file in files)
        {
            SafeRelative(file.Path);Check(Allowed(file.Path,variant=="includeAPI") && file.Length>=0 && file.Length<=UpdateCatalog.MaxDownload && UpdateCatalog.HashValid(file.Sha256),"更新包文件超出白名单或校验记录无效。");
        }
        trace?.Invoke("paths");
        Check(files.Sum(f=>f.Length)+raw.Length<=(variant=="includeAPI" ? 400L : 210L)*1024*1024,"更新包超过发行版的资源预算。");
        var names=files.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(string path in names){string parent=path;while(parent.Contains('/')){parent=parent[..parent.LastIndexOf('/')];Check(!names.Contains(parent),"更新包存在文件与目录冲突。");}}
        trace?.Invoke("complete");
        return new UpdatePackage(version,variant,files,raw);
    }
    public static string SafeRoot(string root)
    {
        Check(Path.IsPathFullyQualified(root),"更新目录必须是绝对路径。");root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Check(!string.Equals(root,Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)),StringComparison.OrdinalIgnoreCase),"不能更新整个磁盘根目录。");
        NativeRuntime.ValidateLocalLocation(root);
        for(string path=root;path!=null;path=Path.GetDirectoryName(path))
        {
            try{Check((File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"更新目录不能经过链接或重解析点。");}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
        }
        return root;
    }
    public static string SafeFile(string root,string relative)
    {
        root=SafeRoot(root);SafeRelative(relative);string path=Path.GetFullPath(Path.Combine(root,relative));
        Check(path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"更新文件越过授权目录。");SafeRoot(Path.GetDirectoryName(path));
        if(File.Exists(path))Check((File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"更新文件不能是链接。");return path;
    }
    internal static byte[] ReadLimited(Stream stream,long maximum)
    {
        using var memory=new MemoryStream();byte[] buffer=new byte[65536];int count;
        while((count=stream.Read(buffer))>0){Check(memory.Length+count<=maximum,"更新元数据超过大小上限。");memory.Write(buffer,0,count);}return memory.ToArray();
    }
    public static string Hash(Stream stream,long expected)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] buffer=new byte[65536];long total=0;int count;
        while((count=stream.Read(buffer))>0){total+=count;Check(total<=expected,"更新文件超过清单大小。");hash.AppendData(buffer,0,count);}
        Check(total==expected,"更新文件长度与清单不符。");return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    public static void VerifyFile(string path,UpdateFile file)
    {
        using var input=File.OpenRead(path);Check(Hash(input,file.Length).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase),"更新文件校验失败："+file.Path);
    }
    public static UpdatePackage Read(string root,string version=null,string variant=null)
    {
        using var input=File.OpenRead(SafeFile(root,"manifest.json"));byte[] raw=ReadLimited(input,4*1024*1024);
        if(version==null || variant==null){using var doc=JsonDocument.Parse(raw);version??=doc.RootElement.GetProperty("version").GetString();variant??=doc.RootElement.GetProperty("variant").GetString();}
        return Parse(raw,version,variant);
    }
    public static UpdatePackage VerifyDirectory(string root,string version,string variant)
    {
        root=SafeRoot(root);using var directories=new UpdateDirectories();directories.Hold(root);
        using var input=UpdateFileIdentity.OpenGuard(directories.File(root,"manifest.json"));var package=Parse(ReadLimited(input,4*1024*1024),version,variant);
        VerifyFiles(root,package.Files);
        return package;
    }
    internal static void VerifyFiles(string root,UpdateFile[] files,Action<int,FileStream> verified=null)
    {
        using var directories=new UpdateDirectories();string[] paths=files.Select(file=>directories.File(root,file.Path)).ToArray();
        try
        {
            Parallel.For(0,files.Length,new ParallelOptions{MaxDegreeOfParallelism=Math.Min(8,Environment.ProcessorCount)},index=>
            {using var held=UpdateLauncher.GuardExecutable(paths[index],files[index]);verified?.Invoke(index,held);});
        }
        catch(AggregateException error){ExceptionDispatchInfo.Capture(error.Flatten().InnerExceptions[0]).Throw();throw;}
    }
    public static UpdatePackage Extract(string archive,string destination,string version,string variant)
    {
        using var input=new FileStream(archive,FileMode.Open,FileAccess.Read,FileShare.Read);return Extract(input,destination,version,variant);
    }
    internal static UpdatePackage Extract(Stream archive,string destination,string version,string variant)
    {
        destination=SafeRoot(destination);Check(!Directory.Exists(destination) && !File.Exists(destination),"更新暂存目录已存在，未覆盖。");
        using var zip=new ZipArchive(archive,ZipArchiveMode.Read,true);Check(zip.Entries.Count is >1 and <=16385,"更新 ZIP 文件数量无效。");
        Check(zip.Entries.Select(e=>e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count()==zip.Entries.Count,"更新 ZIP 包含重名文件。");
        foreach(var entry in zip.Entries)
        {
            SafeRelative(entry.FullName);int kind=(entry.ExternalAttributes>>16)&0xF000;
            Check(kind is 0 or 0x8000 && (entry.ExternalAttributes&(int)FileAttributes.ReparsePoint)==0,"更新 ZIP 包含链接或特殊文件。");
        }
        var manifest=zip.GetEntry("manifest.json")??throw new InvalidDataException("更新 ZIP 缺少清单。");
        Check(manifest.Length is >0 and <=4*1024*1024,"更新 ZIP 清单过大。");
        UpdatePackage package;using(var input=manifest.Open())package=Parse(ReadLimited(input,manifest.Length),version,variant);
        Check(package.Files.Length+1==zip.Entries.Count,"更新 ZIP 存在未列入清单的文件。");
        using var directories=new UpdateDirectories();directories.Hold(destination,true);
        foreach(var file in package.Owned)
        {
            var entry=zip.GetEntry(file.Path)??throw new InvalidDataException("更新 ZIP 缺少所需文件。");Check(entry.Length==file.Length,"更新 ZIP 文件大小不匹配。");
            string target=directories.File(destination,file.Path,true);
            using(var input=entry.Open())using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                byte[] buffer=new byte[65536];long total=0;int count;
                while((count=input.Read(buffer))>0){total+=count;Check(total<=file.Length,"解压文件超过资源预算。");output.Write(buffer,0,count);}Check(total==file.Length,"解压文件长度不符。");
            }
            VerifyFile(target,file);
        }
        return package;
    }
}
