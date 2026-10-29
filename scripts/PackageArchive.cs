using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Popcorn.Release;

public sealed record PackageInput(string Path,string Kind,string Source,string Member,string Checksum,string Content)
{
    public static PackageInput File(string path,string source)=>new(path,"file",source,null,null,null);
    public static PackageInput Zip(string path,string archive,string member,string checksum)=>new(path,"zip",archive,member,checksum,null);
    public static PackageInput Text(string path,string content)=>new(path,"text",null,null,null,content);
}
public sealed record PackageResult(string Version,int Files,long Bytes);

public static class PackageArchive
{
    private const int MaxFiles=2048,MaxManifestBytes=4*1024*1024;
    private const long MaxFileBytes=128L*1024*1024,MaxTotalBytes=512L*1024*1024;
    private static readonly JsonSerializerOptions Json=new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true,
        UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,AllowDuplicateProperties=false,RespectRequiredConstructorParameters=true,MaxDepth=8 };
    private sealed record FileRecord(string Path,long Length,string Sha256);
    private sealed record Manifest(int SchemaVersion,string Version,bool Preview,FileRecord[] Files);
    private static readonly DateTimeOffset Stamp=new(2000,1,1,0,0,0,TimeSpan.Zero);
    private static readonly string[] NativeNames={
        "Api","AutomaticMatch","Core","Desktop","DesktopTray","StartupRegistration","MediaSession","Overlay","OverlayPresentation",
        "Playback","PlaybackEventSignal","PlaybackModels","PlayerControlCache","PlayerSampler","Preferences",
        "ProbeAttempt","ProbeCadence","ProbeFrameReader","ProbeGate","ProbeJob","ProbeOperation","ProbeProcess","ProbeSupervisor",
        "ProbeTransport","ProbeWorkerRuntime","SpriteStore","UiaPlayerControls","Windows"
    };
    public static string[] RequiredPaths=>new[] {
        "启动弹幕助手新版.bat","检查文件.bat","Verify-Package.ps1","README.md","THIRD-PARTY-NOTICES.md",
        "desktop/Host.ps1","desktop/Start.ps1","desktop/ProbeWorker.ps1","ui/dist/index.html","ui/dist/favicon.svg",
        ".deps/pwsh/pwsh.exe",".deps/pwsh/pwsh.dll",".deps/pwsh/System.Management.Automation.dll",".deps/pwsh/System.Private.CoreLib.dll",
        ".deps/pwsh/ref/System.Runtime.dll",".deps/pwsh/LICENSE.txt",".deps/pwsh/ThirdPartyNotices.txt",".deps/pwsh/danmu-runtime.json",
        ".deps/webview2/lib_manual/netcoreapp3.0/Microsoft.Web.WebView2.Core.dll",
        ".deps/webview2/runtimes/win-x64/native/WebView2Loader.dll",
        ".deps/windows-sdk/lib/net8.0/WinRT.Runtime.dll",".deps/windows-sdk/lib/net8.0/Microsoft.Windows.SDK.NET.dll",
        "licenses/WebView2/LICENSE.txt","licenses/WebView2/NOTICE.txt","licenses/WindowsSDK/Windows-SDK.rtf",
        "licenses/WindowsSDK/Microsoft.Windows.SDK.NET.Ref.nuspec","licenses/React/LICENSE.txt",
        "licenses/ReactDOM/LICENSE.txt","licenses/Scheduler/LICENSE.txt"
    }.Concat(NativeNames.Select(n=>"desktop/native/"+n+".cs")).ToArray();

    private static void Check(bool valid,string message) { if(!valid) throw new InvalidDataException(message); }
    private static void Version(string version)=>Check(version!=null && Regex.IsMatch(version,@"^\d+\.\d+\.\d+-preview\.[1-9]\d*$"),"Invalid preview version.");
    private static string Hash(Stream stream,long limit)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer=new byte[65536]; long total=0; int count;
        while((count=stream.Read(buffer,0,buffer.Length))>0)
        {
            total+=count; Check(total<=limit,"Package hash byte budget exceeded.");
            hash.AppendData(buffer,0,count);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static void PathShape(string path)
    {
        Check(!string.IsNullOrEmpty(path) && path.Length<=512 && !path.Contains('\\') && !path.Contains(':') && path.IndexOfAny("<>\"|?*".ToCharArray())<0
            && !path.Any(char.IsControl) && !path.StartsWith('/'),"Invalid package path.");
        foreach(string part in path.Split('/'))
            Check(part.Length>0 && part is not "." and not ".." && !part.EndsWith('.') && !part.EndsWith(' ')
                && !Regex.IsMatch(part,@"^(CON|CONIN\$|CONOUT\$|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(\.|$)",RegexOptions.IgnoreCase),"Invalid Windows package path.");
    }
    private static void Allowed(string path)
    {
        PathShape(path);
        bool allowed=RequiredPaths.Contains(path,StringComparer.Ordinal) ||
            Regex.IsMatch(path,@"^ui/dist/assets/[A-Za-z0-9_-]+\.(js|css|svg|png|jpg|webp|woff2)$") ||
            Regex.IsMatch(path,@"^desktop/native/[A-Za-z0-9]+\.cs$") ||
            path.StartsWith(".deps/pwsh/",StringComparison.Ordinal) ||
            Regex.IsMatch(path,@"^licenses/(WebView2|WindowsSDK|React|ReactDOM|Scheduler)/[A-Za-z0-9_.-]+\.(txt|rtf|json|nuspec)$");
        Check(allowed,"Forbidden package path.");
    }
    private static void Required(IEnumerable<string> names)
    {
        var files=new HashSet<string>(names,StringComparer.Ordinal);
        Check(RequiredPaths.All(files.Contains),"A required package file is missing.");
        Check(files.Any(p=>p.StartsWith("ui/dist/assets/",StringComparison.Ordinal) && p.EndsWith(".js",StringComparison.Ordinal)),
            "A required compiled UI script is missing.");
    }
    private static void SourceFile(string root,string file)
    {
        Check((System.IO.File.GetAttributes(root)&FileAttributes.ReparsePoint)==0,"Reparse project roots cannot be packaged.");
        string full=System.IO.Path.GetFullPath(file),prefix=System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root))+System.IO.Path.DirectorySeparatorChar;
        Check(full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase),"Source file is outside the project root.");
        for(string current=full;current.Length>=prefix.Length;current=System.IO.Path.GetDirectoryName(current))
            Check((System.IO.File.GetAttributes(current)&FileAttributes.ReparsePoint)==0,"Reparse source paths cannot be packaged.");
        Check(System.IO.File.Exists(full),"Source file is missing.");
    }
    private static void SensitiveText(Stream stream,string path,string[] needles)
    {
        if(needles.Length==0 || !Regex.IsMatch(path,@"\.(ps1|cs|js|css|html|json|txt|md|svg|rtf)$",RegexOptions.IgnoreCase)) return;
        Check(stream.Length<=16*1024*1024,"Package text exceeds its inspection budget.");
        using var reader=new StreamReader(stream,Encoding.UTF8,true,4096,true);
        string text=reader.ReadToEnd();
        Check(!needles.Any(needle=>!string.IsNullOrEmpty(needle) && text.Contains(needle,StringComparison.OrdinalIgnoreCase)),
            "Sensitive configuration found in package input.");
        stream.Position=0;
    }

    public static PackageResult Create(string sourceRoot,string destination,string version,PackageInput[] inputs,string[] forbiddenText)
    {
        Version(version); Check(inputs is {Length:>0 and <=MaxFiles},"Invalid input count.");
        Check(forbiddenText!=null,"Missing sensitive-text policy.");
        if(System.IO.File.Exists(destination)) throw new IOException("Package destination already exists.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var input in inputs) Check(input!=null && names.Add(input.Path),"Duplicate package path.");
        foreach(var input in inputs) Allowed(input.Path);
        Required(inputs.Select(i=>i.Path));
        var dependencies=new Dictionary<string,(ZipArchive Archive,string Checksum)>(StringComparer.OrdinalIgnoreCase);
        string temporary=destination+".partial-"+Guid.NewGuid().ToString("N");
        try
        {
            foreach(var input in inputs)
            {
                if(input.Kind=="file") SourceFile(sourceRoot,input.Source);
                else if(input.Kind=="zip")
                {
                    SourceFile(sourceRoot,input.Source); PathShape(input.Member);
                    Check(input.Checksum!=null && Regex.IsMatch(input.Checksum,@"^[a-fA-F0-9]{64}$"),"Missing dependency checksum.");
                    if(!dependencies.ContainsKey(input.Source))
                    {
                        var bytes=System.IO.File.OpenRead(input.Source);
                        try
                        {
                            Check(Hash(bytes,MaxTotalBytes).Equals(input.Checksum,StringComparison.OrdinalIgnoreCase),"Dependency archive checksum mismatch.");
                            bytes.Position=0;
                            dependencies.Add(input.Source,(new ZipArchive(bytes,ZipArchiveMode.Read),input.Checksum));
                        }
                        catch { bytes.Dispose(); throw; }
                    }
                    else
                    {
                        Check(dependencies[input.Source].Checksum.Equals(input.Checksum,StringComparison.OrdinalIgnoreCase),"Inconsistent dependency checksum.");
                    }
                    Check(dependencies[input.Source].Archive.Entries.Count(e=>e.FullName==input.Member)==1,"Dependency member is missing or duplicated.");
                }
                else Check(input.Kind=="text" && input.Content!=null,"Invalid package input kind.");
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination)));
            var records=new List<FileRecord>(); long total=0;
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var archive=new ZipArchive(output,ZipArchiveMode.Create))
            {
                foreach(var input in inputs.OrderBy(i=>i.Path,StringComparer.Ordinal))
                {
                    using Stream data=input.Kind switch {
                        "file"=>System.IO.File.OpenRead(input.Source),
                        "zip"=>dependencies[input.Source].Archive.GetEntry(input.Member).Open(),
                        _=>new MemoryStream(Encoding.UTF8.GetBytes(input.Content))
                    };
                    if(input.Kind!="zip") SensitiveText(data,input.Path,forbiddenText);
                    var entry=archive.CreateEntry(input.Path,CompressionLevel.Optimal); entry.LastWriteTime=Stamp;
                    using var target=entry.Open();
                    using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer=new byte[65536]; long length=0; int count;
                    while((count=data.Read(buffer,0,buffer.Length))>0)
                    {
                        length+=count; total+=count;
                        Check(length<=MaxFileBytes && total<=MaxTotalBytes,"Package byte budget exceeded.");
                        hash.AppendData(buffer,0,count); target.Write(buffer,0,count);
                    }
                    records.Add(new(input.Path,length,Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
                }
                var manifest=archive.CreateEntry("manifest.json",CompressionLevel.Optimal); manifest.LastWriteTime=Stamp;
                using var writer=new StreamWriter(manifest.Open(),new UTF8Encoding(false));
                writer.Write(JsonSerializer.Serialize(new Manifest(1,version,true,records.ToArray()),Json));
            }
            var result=Verify(temporary);
            System.IO.File.Move(temporary,destination);
            return result;
        }
        finally { foreach(var dependency in dependencies.Values) dependency.Archive.Dispose(); }
    }

    public static PackageResult Verify(string path)
    {
        using var archive=ZipFile.OpenRead(path);
        Check(archive.Entries.Count is >1 and <=MaxFiles+1,"Invalid manifest entry count.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in archive.Entries) Check(names.Add(entry.FullName),"Duplicate ZIP entry.");
        foreach(var entry in archive.Entries) if(entry.FullName!="manifest.json") Allowed(entry.FullName);
        var raw=archive.GetEntry("manifest.json");
        Check(raw!=null && raw.Length<=MaxManifestBytes,"Missing or oversized manifest.");
        Manifest manifest;
        try { using var data=raw.Open(); manifest=JsonSerializer.Deserialize<Manifest>(data,Json); }
        catch(JsonException) { throw new InvalidDataException("Invalid package manifest."); }
        Check(manifest is {SchemaVersion:1,Preview:true,Files.Length:>0 and <=MaxFiles},"Unsupported package manifest.");
        Check(manifest.Files.All(f=>f!=null),"Invalid null manifest record.");
        Version(manifest.Version); Required(manifest.Files.Select(f=>f.Path));
        var records=new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total=0;
        foreach(var file in manifest.Files)
        {
            Check(file!=null && records.Add(file.Path),"Duplicate manifest path.");
            Allowed(file.Path);
            var entry=archive.GetEntry(file.Path);
            Check(entry!=null && file.Length>=0 && file.Length<=MaxFileBytes,"Package manifest references a missing or invalid file.");
            Check(entry.Length==file.Length,"Package file length/hash mismatch.");
            using var data=entry.Open();
            Check(file.Sha256!=null && Regex.IsMatch(file.Sha256,@"^[a-f0-9]{64}$") && Hash(data,file.Length)==file.Sha256,"Package file hash mismatch.");
            total+=file.Length; Check(total<=MaxTotalBytes,"Package byte budget exceeded.");
        }
        Check(archive.Entries.Count==manifest.Files.Length+1,"ZIP entries do not match the manifest.");
        return new(manifest.Version,manifest.Files.Length,total);
    }
}
