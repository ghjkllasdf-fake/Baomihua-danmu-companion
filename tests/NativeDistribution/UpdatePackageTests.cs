using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Popcorn;

internal static class UpdatePackageTests
{
    internal static byte[] Manifest(byte[] payload,string path="DanmuCompanion.exe",string version="0.1.1-beta.10",string variant="standard",bool dirty=false)=>
        JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=1,version,variant,sourceCommit=new string('a',40),sourceDirty=dirty,
            files=new[]{new{path,length=payload.Length,sha256=Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()}}});
    internal static void Reject(Action action,string message)
    {
        bool rejected=false;try{action();}catch(Exception e) when(e is InvalidDataException or ArgumentException or IOException){rejected=true;}
        DistributionTests.Check(rejected,message);
    }
    internal static void Run()
    {
        byte[] bytes="controlled fixture, never executed"u8.ToArray();
        var manifest=UpdatePackage.Parse(Manifest(bytes),"0.1.1-beta.10","standard");
        DistributionTests.Check(manifest.Files.Length==1,"Manifest must retain owned file records.");
        foreach(string path in new[]{"../escape","C:/escape","a\\b","CON.exe","a./b",".git/config","preferences.json","api.dat","a/b/../c"})
            Reject(()=>UpdatePackage.Parse(Manifest(bytes,path),"0.1.1-beta.10","standard"),"Unsafe package path accepted: "+path);
        Reject(()=>UpdatePackage.Parse(Manifest(bytes,dirty:true),"0.1.1-beta.10","standard"),"Dirty candidate must not be installed as a public update.");
        Reject(()=>UpdatePackage.Parse(Manifest(bytes,variant:"includeAPI"),"0.1.1-beta.10","standard"),"Package edition disagreement must fail closed.");
        Reject(()=>UpdatePackage.Parse(Manifest(bytes),"0.1.1-beta.9","standard"),"Package version disagreement must fail closed.");
        string root=Path.Combine(Path.GetTempPath(),"DanmuUpdatePackage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string zip=Path.Combine(root,"sample.zip");
            using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create))
            {
                using(var stream=archive.CreateEntry("manifest.json").Open())stream.Write(Manifest(bytes));
                using(var stream=archive.CreateEntry("DanmuCompanion.exe").Open())stream.Write(bytes);
            }
            string stage=Path.Combine(root,"stage");
            UpdatePackage.Extract(zip,stage,"0.1.1-beta.10","standard");
            UpdatePackage.VerifyDirectory(stage,"0.1.1-beta.10","standard");
            DistributionTests.Check(File.ReadAllBytes(Path.Combine(stage,"DanmuCompanion.exe")).AsSpan().SequenceEqual(bytes),"Extracted bytes must match the manifest.");
            Reject(()=>UpdatePackage.Extract(zip,stage,"0.1.1-beta.10","standard"),"Extraction must never overwrite an existing directory.");
            File.WriteAllText(Path.Combine(stage,"DanmuCompanion.exe"),"tampered");
            Reject(()=>UpdatePackage.VerifyDirectory(stage,"0.1.1-beta.10","standard"),"Changed staged file must be rejected.");
            string linkZip=Path.Combine(root,"symlink.zip");
            using(var archive=ZipFile.Open(linkZip,ZipArchiveMode.Create))
            {
                using(var stream=archive.CreateEntry("manifest.json").Open())stream.Write(Manifest(bytes));
                var entry=archive.CreateEntry("DanmuCompanion.exe");entry.ExternalAttributes=unchecked((int)0xA1FF0000);
                using(var stream=entry.Open())stream.Write(bytes);
            }
            Reject(()=>UpdatePackage.Extract(linkZip,Path.Combine(root,"link-stage"),"0.1.1-beta.10","standard"),"ZIP symlinks must be rejected.");
            DistributionTests.Check(!File.Exists(Path.Combine(root,"escape")),"Rejected extraction must not write outside its stage.");
        }
        finally
        {
            if(!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath())+"DanmuUpdatePackage-",StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe fixture cleanup root.");
            Directory.Delete(root,true);
        }
        Console.WriteLine("PASS: update manifest and archive safety contracts.");
    }
}
