using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Popcorn;

internal static class UpdateTransactionTests
{
    internal static void Payload(string root,string version,Dictionary<string,string> files)
    {
        Directory.CreateDirectory(root);
        foreach(var pair in files){string path=UpdatePackage.SafeFile(root,pair.Key);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,pair.Value,new UTF8Encoding(false));}
        File.WriteAllBytes(Path.Combine(root,"manifest.json"),JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=1,version,variant="standard",sourceCommit=new string('a',40),sourceDirty=false,
            files=files.Select(pair=>new{path=pair.Key,length=Encoding.UTF8.GetByteCount(pair.Value),sha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value))).ToLowerInvariant()}).ToArray()}));
    }
    internal static void Run()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"DanmuUpdateTransaction-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        try
        {
            string root=Path.Combine(fixture,"application"),parent=Path.Combine(fixture,".DanmuCompanion-updates");
            var oldFiles=new Dictionary<string,string>{{"DanmuCompanion.exe","old controlled fixture"},{"old.dll","old owned library"}};
            var nextFiles=new Dictionary<string,string>{{"DanmuCompanion.exe","new controlled fixture"},{"new.dll","new owned library"}};
            Payload(root,"0.1.1-beta.9",oldFiles);File.WriteAllText(Path.Combine(root,"user.txt"),"preserve foreign file");
            string directory=UpdateStorage.CreateOperation(parent,"transaction"),stage=Path.Combine(directory,"stage");Payload(stage,"0.1.1-beta.10",nextFiles);
            var transaction=UpdateTransaction.Prepare(root,directory,"0.1.1-beta.10","standard");
            transaction.Apply();UpdatePackage.VerifyDirectory(root,"0.1.1-beta.10","standard");
            DistributionTests.Check(!File.Exists(Path.Combine(root,"old.dll")) && File.Exists(Path.Combine(root,"new.dll")),"Apply must reconcile only owned files.");
            DistributionTests.Check(File.ReadAllText(Path.Combine(root,"user.txt"))=="preserve foreign file","Foreign files must survive a portable update.");
            transaction.Restore();UpdatePackage.VerifyDirectory(root,"0.1.1-beta.9","standard");
            DistributionTests.Check(!File.Exists(Path.Combine(root,"new.dll")),"Startup failure rollback must remove only newly owned files.");
            string interrupted=UpdateStorage.CreateOperation(parent,"transaction");Payload(Path.Combine(interrupted,"stage"),"0.1.1-beta.10",nextFiles);
            var rollback=UpdateTransaction.Prepare(root,interrupted,"0.1.1-beta.10","standard");
            UpdatePackageTests.Reject(()=>rollback.Apply(_=>throw new IOException("injected apply failure")),"Apply failure must remain a failure after rollback.");
            UpdatePackage.VerifyDirectory(root,"0.1.1-beta.9","standard");
            DistributionTests.Check(File.ReadAllText(Path.Combine(root,"user.txt"))=="preserve foreign file","Rollback must preserve foreign content.");
            File.Copy(Path.Combine(interrupted,"stage","new.dll"),Path.Combine(root,"new.dll"));
            UpdateTransaction.Load(interrupted).Restore();
            DistributionTests.Check(!File.Exists(Path.Combine(root,"new.dll")),"Journal recovery must clean a partially applied owned file.");
            File.WriteAllText(Path.Combine(root,"new.dll"),"foreign collision");
            string collision=UpdateStorage.CreateOperation(parent,"transaction");Payload(Path.Combine(collision,"stage"),"0.1.1-beta.10",nextFiles);
            UpdatePackageTests.Reject(()=>UpdateTransaction.Prepare(root,collision,"0.1.1-beta.10","standard"),"New payload must not overwrite an unowned collision.");
            DistributionTests.Check(File.ReadAllText(Path.Combine(root,"new.dll"))=="foreign collision","Refused update must preserve the colliding file.");
            File.WriteAllText(Path.Combine(root,"DanmuCompanion.exe"),"user modification");
            UpdatePackageTests.Reject(()=>UpdateTransaction.Prepare(root,collision,"0.1.1-beta.10","standard"),"Modified original owned bytes must not be silently overwritten.");
            UpdatePackageTests.Reject(()=>UpdateStorage.RemoveOwned(fixture,root),"Cleanup must reject an application root without an operation marker.");
        }
        finally
        {
            if(!Path.GetFullPath(fixture).StartsWith(Path.GetFullPath(Path.GetTempPath())+"DanmuUpdateTransaction-",StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe fixture cleanup root.");
            Directory.Delete(fixture,true);
        }
        Console.WriteLine("PASS: portable apply, rollback, interrupted journal recovery and ownership guards.");
    }
}
