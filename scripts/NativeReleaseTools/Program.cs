using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class ReleaseTools
{
    private const long MaxFile=512L*1024*1024,MaxTotal=2L*1024*1024*1024;
    private sealed record FileRecord(string Path,long Length,string Sha256);
    private static void Check(bool value,string message){if(!value)throw new InvalidDataException(message);}
    private static void SafePath(string path)
    {
        Check(path.Length is >0 and <512 && !path.StartsWith('/') && !path.Contains('\\') &&
            path.IndexOfAny(":<>\"|?*".ToCharArray())<0 && !path.Any(char.IsControl),"Unsafe package path.");
        Check(path.Split('/').All(p=>p.Length>0 && p is not "." and not ".." && !p.EndsWith('.') && !p.EndsWith(' ') &&
            !Regex.IsMatch(p,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)",RegexOptions.IgnoreCase)),"Unsafe Windows package path.");
    }
    private static string Hash(Stream stream,long expected)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] buffer=new byte[65536];long total=0;int count;
        while((count=stream.Read(buffer))>0){total+=count;Check(total<=expected,"Payload length exceeded manifest.");hash.AppendData(buffer,0,count);}
        Check(total==expected,"Payload length differs from manifest.");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static FileRecord[] Manifest(Stream stream,long length)
    {
        Check(length is >0 and <=4*1024*1024,"Invalid manifest size.");
        using var memory=new MemoryStream();var buffer=new byte[65536];int count;
        while((count=stream.Read(buffer))>0){Check(memory.Length+count<=length,"Manifest length exceeded.");memory.Write(buffer,0,count);}
        using var document=JsonDocument.Parse(memory.ToArray(),new JsonDocumentOptions{MaxDepth=16});
        var root=document.RootElement;
        Check(root.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==root.EnumerateObject().Count(),"Duplicate manifest properties.");
        Check(root.GetProperty("schemaVersion").GetInt32()==1 && Regex.IsMatch(root.GetProperty("version").GetString()??"",@"^\d+\.\d+\.\d+-beta\.[1-9]\d*$"),"Unsupported beta manifest.");
        Check(Regex.IsMatch(root.GetProperty("sourceCommit").GetString()??"",@"^[a-f0-9]{40}$"),"Missing source identity.");
        var records=root.GetProperty("files").EnumerateArray().Select(file=>
            new FileRecord(file.GetProperty("path").GetString()??"",file.GetProperty("length").GetInt64(),file.GetProperty("sha256").GetString()??"")).ToArray();
        Check(records.Length is >0 and <=4096,"Invalid payload count.");
        Check(records.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()==records.Length,"Duplicate manifest file.");
        long total=0;
        foreach(var file in records){SafePath(file.Path);Check(file.Path!="manifest.json" && file.Length is >=0 and <=MaxFile && Regex.IsMatch(file.Sha256,@"^[a-f0-9]{64}$"),"Invalid file record.");total+=file.Length;}
        Check(total<=MaxTotal,"Payload exceeds its byte budget.");return records;
    }
    private static IEnumerable<string> Files(string root)
    {
        Check((File.GetAttributes(root)&FileAttributes.ReparsePoint)==0,"Reparse payload root.");
        foreach(string path in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes=File.GetAttributes(path);Check((attributes&FileAttributes.ReparsePoint)==0,"Reparse payload entry.");
            if((attributes&FileAttributes.Directory)!=0){foreach(string child in Files(path))yield return child;}
            else yield return path;
        }
    }
    private static int VerifyDirectory(string root,bool installed=false)
    {
        root=Path.GetFullPath(root);string manifest=Path.Combine(root,"manifest.json");
        using var input=File.OpenRead(manifest);var records=Manifest(input,input.Length);
        if(!installed)Check(Files(root).Count()==records.Length+1,"Unexpected directory payload files.");
        foreach(var record in records)
        {
            string path=Path.Combine(root,record.Path);using var stream=File.OpenRead(path);
            Check(Hash(stream,record.Length)==record.Sha256,"Directory payload hash mismatch: "+record.Path);
        }
        return records.Length;
    }
    private static int VerifyZip(string path)
    {
        using var zip=ZipFile.OpenRead(path);Check(zip.Entries.Count is >1 and <=4097,"Invalid ZIP entry count.");
        Check(zip.Entries.Select(e=>e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count()==zip.Entries.Count,"Duplicate ZIP paths.");
        foreach(var entry in zip.Entries)SafePath(entry.FullName);
        var raw=zip.GetEntry("manifest.json")??throw new InvalidDataException("Missing manifest.");
        using var manifest=raw.Open();var records=Manifest(manifest,raw.Length);
        Check(records.Length+1==zip.Entries.Count,"Unmanifested ZIP entry.");
        foreach(var record in records)
        {
            var entry=zip.GetEntry(record.Path)??throw new InvalidDataException("Missing ZIP file.");
            Check(entry.Length==record.Length,"ZIP file length mismatch.");using var data=entry.Open();
            Check(Hash(data,record.Length)==record.Sha256,"ZIP payload hash mismatch: "+record.Path);
        }
        return records.Length;
    }
    private static int Pack(string root,string destination)
    {
        root=Path.GetFullPath(root);destination=Path.GetFullPath(destination);int count=VerifyDirectory(root);
        Check(!destination.StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"ZIP must be outside payload.");
        if(File.Exists(destination))throw new IOException("ZIP destination already exists.");
        string temporary=destination+"."+Guid.NewGuid().ToString("N")+".partial";
        try
        {
            using(var output=new FileStream(temporary,FileMode.CreateNew))
            using(var zip=new ZipArchive(output,ZipArchiveMode.Create))
            {
                foreach(string file in Files(root).Order(StringComparer.Ordinal))
                {
                    string relative=Path.GetRelativePath(root,file).Replace('\\','/');SafePath(relative);
                    var entry=zip.CreateEntry(relative,CompressionLevel.Optimal);entry.LastWriteTime=new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero);
                    using var data=entry.Open();using var source=File.OpenRead(file);source.CopyTo(data);
                }
            }
            VerifyZip(temporary);File.Move(temporary,destination);return count;
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            int files;
            if(args is ["pack",var root,var destination])files=Pack(root,destination);
            else if(args is ["verify",var archive])files=VerifyZip(archive);
            else if(args is ["verify-dir",var directory])files=VerifyDirectory(directory);
            else if(args is ["verify-installed",var installed])files=VerifyDirectory(installed,true);
            else if(args is ["smoke",var app,var data,var report]){ProcessSmoke.Run(app,data,report);files=0;}
            else if(args is ["check-shortcut",var shortcut,var executable]){ShortcutCheck.Run(shortcut,executable);files=0;}
            else if(args is ["extract",var source,var target])
            {
                files=VerifyZip(source);if(Directory.Exists(target) || File.Exists(target))throw new IOException("Extraction destination already exists.");
                ZipFile.ExtractToDirectory(source,target);VerifyDirectory(target);
            }
            else throw new ArgumentException("Expected pack, verify, verify-dir, verify-installed, extract, smoke or check-shortcut arguments.");
            Console.WriteLine(JsonSerializer.Serialize(new{passed=true,files}));return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
}
