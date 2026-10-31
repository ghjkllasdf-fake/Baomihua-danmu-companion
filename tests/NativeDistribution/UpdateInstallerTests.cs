using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Popcorn;

internal static class UpdateInstallerTests
{
    internal static int Sized(string root,string stage,string output)
    {
        string parent=UpdatePackage.SafeRoot(Path.Combine(Directory.GetCurrentDirectory(),"artifacts","updater-sized-tests"))+Path.DirectorySeparatorChar;
        UpdatePackage.Check(new[]{root,stage,output}.All(path=>Path.GetFullPath(path).StartsWith(parent,StringComparison.OrdinalIgnoreCase)),"Sized fixture is restricted to owned test directories.");
        var timer=Stopwatch.StartNew();long allocated=GC.GetTotalAllocatedBytes();Console.WriteLine("SIZED: reading manifest");Console.Out.Flush();
        var next=UpdatePackage.Parse(File.ReadAllBytes(Path.Combine(stage,"manifest.json")),"0.1.1-beta.7","includeAPI",phase=>{Console.WriteLine($"SIZED: parse-{phase} at {timer.ElapsedMilliseconds}ms");Console.Out.Flush();});
        UpdatePackage.Check(next.Files.Length>=4000 && next.Variant=="includeAPI","Benchmark must use actual includeAPI-scale files, not a small fixture.");
        string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update");
        var apply=UpdateApply.Prepare(root,directory,stage,next.Version,next.Variant,phase=>{Console.WriteLine($"SIZED: {phase} at {timer.ElapsedMilliseconds}ms");Console.Out.Flush();});long prepareMs=timer.ElapsedMilliseconds;
        apply.Apply(count=>{if(count%1000==0){Console.WriteLine($"SIZED: committed {count} at {timer.ElapsedMilliseconds}ms");Console.Out.Flush();}});timer.Stop();
        using var process=Process.GetCurrentProcess();int records=Directory.GetFiles(directory,"*.file-id").Length;
        File.WriteAllBytes(output,JsonSerializer.SerializeToUtf8Bytes(new{productionRelease=false,fileCount=next.Owned.Length,payloadBytes=next.Bytes,prepareMs,totalMs=timer.ElapsedMilliseconds,
            peakWorkingSetBytes=process.PeakWorkingSet64,allocatedBytes=GC.GetTotalAllocatedBytes()-allocated,incrementalIdentityRecords=records,metadataScope=directory,backupCreated=Directory.Exists(Path.Combine(directory,"backup"))}));
        UpdatePackage.Check(records==next.Owned.Length,"Each new file must have exactly one incremental identity record.");return 0;
    }
    internal static int Fixture(string[] args)
    {
        if(args.Length==5 && args[1]=="install-current")
        {
            string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(args[2]),"direct-update");
            var apply=UpdateApply.PrepareInstall(args[2],directory,args[3]);apply.Apply();File.WriteAllText(args[4],directory);return 0;
        }
        if(args.Length==5 && args[1]=="repair-current"){UpdateApply.Load(args[3],args[4]).RepairCurrent();return 0;}
        if(args.Length==3 && args[1]=="finish"){var apply=UpdateApply.Load(args[2]);apply.AcceptInstalled();apply.CompleteMetadata();UpdateStorage.RemoveOwned(UpdateInstalledOwnership.ParentFor(apply.Root),apply.Directory);return 0;}
        if(args.Length==5 && args[1]=="prepare")
        {
            var next=UpdatePackage.Read(args[3]);UpdateApply.Prepare(args[2],args[4],args[3],next.Version,next.Variant);return 0;
        }
        if(args.Length==3 && args[1]=="create-new"){UpdateApply.Load(args[2]).Apply();return 0;}
        if(args.Length==3 && args[1]=="partial")
        {try{UpdateApply.Load(args[2]).Apply(count=>{if(count==1)throw new IOException("Controlled partial write");});}catch(IOException){return 0;}return 1;}
        if(args.Length==4 && args[1]=="repair-proof-gap")
        {try{UpdateApply.Load(args[2]).RepairCurrent(path=>{if(path==args[3])throw new IOException("Controlled repair proof interruption");});}catch(IOException){return 0;}return 1;}
        if(args.Length==4 && args[1]=="delete-gap")
        {
            try{UpdateApply.Load(args[2]).Apply(afterRemove:path=>{if(path==args[3])throw new IOException("Controlled repair gap");});}catch(IOException){return 0;}return 1;
        }
        if(args.Length==3 && args[1]=="accept"){UpdateApply.Load(args[2]).AcceptInstalled();return 0;}
        if(args.Length==3 && args[1]=="record")
        {
            var package=UpdatePackage.Read(args[2]);UpdateInstalledOwnership.Save(args[2],package.Owned.Select(f=>new UpdateOwnedFile(f.Path,UpdateFileIdentity.Read(UpdatePackage.SafeFile(args[2],f.Path)))).ToArray());return 0;
        }
        throw new ArgumentException("Invalid direct installer fixture options; old-version recovery is not supported.");
    }
    internal static void Run()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"DanmuUpdateInstaller-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        try
        {
            OwnershipPermissions(fixture);
            string root=Path.Combine(fixture,"app"),stage=Path.Combine(fixture,"stage");
            UpdateTransactionTests.Payload(root,"0.1.1-beta.6",new(){{"DanmuCompanion.exe","old"}});
            File.WriteAllText(Path.Combine(root,"unins000.exe"),"existing uninstall registration bytes");File.WriteAllText(Path.Combine(root,"user.txt"),"foreign");
            UpdateTransactionTests.Payload(stage,"0.1.1-beta.7",new(){{"DanmuCompanion.exe","new"},{"new.dll","new dll"}});
            string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update");
            UpdateApply.Prepare(root,directory,stage,"0.1.1-beta.7","standard");var loaded=UpdateApply.Load(directory);loaded.Apply();
            DistributionTests.Check(!Directory.Exists(Path.Combine(directory,"backup")) && !File.Exists(Path.Combine(directory,"registration.json")),"Installer path must not backup payload, uninstaller or registry.");
            DistributionTests.Check(File.ReadAllText(Path.Combine(root,"unins000.exe"))=="existing uninstall registration bytes" && UpdateInstalledOwnership.Load(root).Length==3,"All native-written payload paths must have disk-identity uninstall ownership.");
            loaded.CompleteMetadata();string successor=Path.Combine(root,"new.dll");File.Move(successor,successor+".foreign");File.WriteAllText(successor,"new dll");
            string nextStage=Path.Combine(fixture,"next-stage");UpdateTransactionTests.Payload(nextStage,"0.1.1-beta.8",new(){{"DanmuCompanion.exe","next"},{"new.dll","next dll"}});
            UpdatePackageTests.Reject(()=>UpdateApply.Prepare(root,UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update"),nextStage,"0.1.1-beta.8","standard"),"Identical foreign successors must not be re-owned across generations.");
            File.WriteAllText(Path.Combine(directory,"journal.json"),"{\"schemaVersion\":1,\"kind\":\"installer\"}");UpdatePackageTests.Reject(()=>UpdateApply.Load(directory),"Legacy journals must never write old bytes into a newer root.");
        }
        finally{UpdatePackage.Check(Path.GetFullPath(fixture).StartsWith(Path.Combine(Path.GetTempPath(),"DanmuUpdateInstaller-"),StringComparison.OrdinalIgnoreCase),"Unsafe fixture cleanup.");Directory.Delete(fixture,true);}
        Console.WriteLine("PASS: installer direct-overwrite metadata, no registry/uninstaller backup, all-file ownership and legacy-record refusal.");
    }
    private static void OwnershipPermissions(string fixture)
    {
        foreach(string mode in new[]{"delete-denied","create-denied"})
        {
            string root=Path.Combine(fixture,mode,"app"),stage=Path.Combine(fixture,mode,"stage");
            UpdateTransactionTests.Payload(root,"0.1.1-beta.6",new(){{"DanmuCompanion.exe","old"}});
            UpdateTransactionTests.Payload(stage,"0.1.1-beta.7",new(){{"DanmuCompanion.exe","new"}});
            var original=UpdatePackage.Read(root);UpdateInstalledOwnership.Save(root,original.Owned.Select(f=>new UpdateOwnedFile(f.Path,UpdateFileIdentity.Read(UpdatePackage.SafeFile(root,f.Path)))).ToArray());
            string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update");var apply=UpdateApply.Prepare(root,directory,stage,"0.1.1-beta.7","standard");
            var parent=new DirectoryInfo(UpdateInstalledOwnership.ParentFor(root));var sidecar=new FileInfo(UpdateInstalledOwnership.PathFor(root));
            UpdatePackage.Check(parent.FullName.StartsWith(Path.GetFullPath(fixture)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"ACL tests must stay inside the isolated fixture.");
            var parentAcl=parent.GetAccessControl(AccessControlSections.Access);var fileAcl=sidecar.GetAccessControl(AccessControlSections.Access);
            string savedParent=parentAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Access),savedFile=fileAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            using var identity=WindowsIdentity.GetCurrent();
            try
            {
                if(mode=="delete-denied")
                {
                    parentAcl.AddAccessRule(new(identity.User,FileSystemRights.DeleteSubdirectoriesAndFiles,AccessControlType.Deny));parent.SetAccessControl(parentAcl);
                    fileAcl.AddAccessRule(new(identity.User,FileSystemRights.Delete,AccessControlType.Deny));sidecar.SetAccessControl(fileAcl);
                }
                else {parentAcl.AddAccessRule(new(identity.User,FileSystemRights.CreateFiles,AccessControlType.Deny));parent.SetAccessControl(parentAcl);}
                bool denied=false;try{apply.Apply();}catch(Exception error) when(UpdateStorage.MetadataError(error)){denied=true;}
                DistributionTests.Check(denied && !apply.Mutated && File.ReadAllText(Path.Combine(root,"DanmuCompanion.exe"))=="old","Ownership ACL failure must be detected before payload mutation: "+mode);
                var retry=UpdateApply.Load(directory);denied=false;try{retry.RepairCurrent();}catch(Exception error) when(UpdateStorage.MetadataError(error)){denied=true;}
                DistributionTests.Check(denied && !retry.Mutated,"Repair must preflight the actual sidecar parent and DELETE permission: "+mode);
            }
            finally
            {
                parentAcl.SetSecurityDescriptorSddlForm(savedParent,AccessControlSections.Access);parent.SetAccessControl(parentAcl);
                fileAcl.SetSecurityDescriptorSddlForm(savedFile,AccessControlSections.Access);sidecar.SetAccessControl(fileAcl);
            }
            apply.Apply();apply.AcceptInstalled();
        }
    }
}
