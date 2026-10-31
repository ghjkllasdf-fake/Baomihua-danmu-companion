using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Popcorn;

internal static class UpdateInstallerTests
{
    internal static int Sized(string root,string stage,string output,bool fresh=false)
    {
        string parent=UpdatePackage.SafeRoot(Path.Combine(Directory.GetCurrentDirectory(),"artifacts","updater-sized-tests"))+Path.DirectorySeparatorChar;
        UpdatePackage.Check(new[]{root,stage,output}.All(path=>Path.GetFullPath(path).StartsWith(parent,StringComparison.OrdinalIgnoreCase)),"Sized fixture is restricted to owned test directories.");
        var timer=Stopwatch.StartNew();long allocated=GC.GetTotalAllocatedBytes();Console.WriteLine("SIZED: reading manifest");Console.Out.Flush();
        var next=UpdatePackage.Parse(File.ReadAllBytes(Path.Combine(stage,"manifest.json")),"0.1.1-beta.7","includeAPI",phase=>{Console.WriteLine($"SIZED: parse-{phase} at {timer.ElapsedMilliseconds}ms");Console.Out.Flush();});
        UpdatePackage.Check(next.Files.Length>=4000 && next.Variant=="includeAPI","Benchmark must use actual includeAPI-scale files, not a small fixture.");
        string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update");
        Action<string> trace=phase=>{Console.WriteLine($"SIZED: {phase} at {timer.ElapsedMilliseconds}ms");Console.Out.Flush();};
        var apply=fresh ? UpdateApply.PrepareInstall(root,directory,stage,trace) : UpdateApply.Prepare(root,directory,stage,next.Version,next.Variant,trace);long prepareMs=timer.ElapsedMilliseconds;
        apply.Apply(count=>{if(count%1000==0){Console.WriteLine($"SIZED: committed {count} at {timer.ElapsedMilliseconds}ms");Console.Out.Flush();}});timer.Stop();
        using var process=Process.GetCurrentProcess();int records=Directory.GetFiles(directory,"*.file-id").Length;
        File.WriteAllBytes(output,JsonSerializer.SerializeToUtf8Bytes(new{productionRelease=false,fileCount=next.Owned.Length,payloadBytes=next.Bytes,prepareMs,totalMs=timer.ElapsedMilliseconds,
            freshInstall=fresh,peakWorkingSetBytes=process.PeakWorkingSet64,allocatedBytes=GC.GetTotalAllocatedBytes()-allocated,incrementalIdentityRecords=records,metadataScope=directory,backupCreated=Directory.Exists(Path.Combine(directory,"backup"))}));
        UpdatePackage.Check(records==next.Owned.Length,"Each new file must have exactly one incremental identity record.");return 0;
    }
    internal static int Fixture(string[] args)
    {
        if(args.Length==6 && args[1]=="kill-directory")
        {
            string root=UpdatePackage.SafeRoot(args[2]),directory=UpdatePackage.SafeRoot(args[3]);
            UpdatePackage.Check(root.StartsWith(Path.Combine(Path.GetTempPath(),"DanmuUpdateInstaller-"),StringComparison.OrdinalIgnoreCase),"Crash fixture must remain in its test workspace.");
            var apply=UpdateApply.Load(directory);apply.SetPhase("applying");var authority=new UpdateMetadataAuthority(root,directory,false);
            using var directories=new UpdateDirectories((path,id)=>{if(args[5]=="after-proof")authority.RecordDirectory(path,id);File.WriteAllText(args[4],id);Environment.Exit(23);});
            directories.Hold(Path.Combine(root,"licenses"),true);return 1;
        }
        if(args.Length==5 && args[1]=="hold")
        {
            string parent=Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),"artifacts","updater-installer-tests"))+Path.DirectorySeparatorChar;
            UpdatePackage.Check(args.Skip(2).All(path=>Path.GetFullPath(path).StartsWith(parent,StringComparison.OrdinalIgnoreCase)),"Lock fixture must remain in the test workspace.");
            using var directories=Directory.Exists(args[2]) ? new UpdateDirectories() : null;
            using var file=directories==null ? new FileStream(args[2],FileMode.Open,FileAccess.Read,FileShare.Read) : null;
            directories?.Hold(args[2]);File.WriteAllText(args[3],"ready");var timer=Stopwatch.StartNew();
            while(!File.Exists(args[4]) && timer.Elapsed<TimeSpan.FromSeconds(30))Thread.Sleep(50);return 0;
        }
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
            MissingOwnership(fixture);
            ReadSharedLocks(fixture);
            PortableRegistration(fixture);
            MetadataRepairAuthority(fixture);
            ReadonlyAndDirectoryConflicts(fixture);
            OwnershipPermissions(fixture);
            FreshDirectories(fixture);
            DurableDirectoryRepair(fixture);
            DirectoryCrashProofs(fixture);
            CleanupQueryFailure(fixture);
            CleanupOriginAndRoot(fixture);
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
    private static void MissingOwnership(string fixture)
    {
        foreach(string mode in new[]{"missing","incomplete","complete","in-place-forged"})foreach(bool manual in new[]{false,true})
        {
            string parent=Path.Combine(fixture,"ownership-"+mode+"-"+manual),root=Path.Combine(parent,"app"),stage=Path.Combine(parent,"stage");
            UpdateTransactionTests.Payload(root,"0.1.1-beta.7",new(){{"DanmuCompanion.exe","old"},{"stable.dll","same bytes"}});
            UpdateTransactionTests.Payload(stage,"0.1.1-beta.8",new(){{"DanmuCompanion.exe","new"},{"stable.dll","changed bytes"}});
            var original=UpdatePackage.Read(root);UpdateInstalledOwnership.Save(root,original.Owned.Select(f=>new UpdateOwnedFile(f.Path,UpdateFileIdentity.Read(UpdatePackage.SafeFile(root,f.Path)))).ToArray());
            string sidecar=UpdateInstalledOwnership.PathFor(root);
            if(mode=="missing")File.Move(sidecar,sidecar+".saved");
            if(mode=="incomplete")File.WriteAllLines(sidecar,File.ReadAllLines(sidecar).Where(line=>!line.StartsWith("stable.dll|",StringComparison.Ordinal)));
            string stable=Path.Combine(root,"stable.dll");File.Move(stable,stable+".saved");File.WriteAllText(stable,"same bytes");string foreignId=UpdateFileIdentity.Read(stable);
            if(mode=="in-place-forged")File.WriteAllLines(sidecar,File.ReadAllLines(sidecar).Select(line=>line.StartsWith("stable.dll|",StringComparison.Ordinal) ? "stable.dll|"+foreignId : line));
            string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update");
            UpdatePackageTests.Reject(()=>{var apply=manual ? UpdateApply.PrepareInstall(root,directory,stage) : UpdateApply.Prepare(root,directory,stage,"0.1.1-beta.8","standard");apply.Apply();},
                "Missing, incomplete or mismatching migrated ownership must reject a same-byte foreign successor before mutation: "+mode+"/"+manual);
            DistributionTests.Check(File.ReadAllText(Path.Combine(root,"DanmuCompanion.exe"))=="old" && UpdateFileIdentity.Read(stable)==foreignId,"Ownership failure must leave every original/foreign payload object unchanged.");
        }
    }
    private static void FreshDirectories(string fixture)
    {
        foreach(bool collision in new[]{false,true})
        {
            string root=Path.Combine(fixture,"fresh-directories-"+collision,"app"),stage=root+"-stage";
            UpdateTransactionTests.Payload(stage,"0.1.1-beta.7",new(){{"DanmuCompanion.exe","main"},{"licenses/component/notice.txt","notice"}});
            string operation=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update");
            var apply=UpdateApply.PrepareInstall(root,operation,stage);
            if(collision)
            {
                File.WriteAllText(Path.Combine(root,"foreign.txt"),"foreign sentinel");
                UpdatePackageTests.Reject(()=>apply.Apply(),"A file introduced into a fresh prepared root must stop all payload writes.");
                DistributionTests.Check(!File.Exists(Path.Combine(root,"DanmuCompanion.exe")) && File.ReadAllText(Path.Combine(root,"foreign.txt"))=="foreign sentinel","A fresh-root collision must remain untouched.");
            }
            else
            {
                apply.Apply();UpdateRegistration.RequireReady(root,UpdatePackage.Read(root));
                DistributionTests.Check(File.ReadAllText(Path.Combine(root,"licenses/component/notice.txt"))=="notice","Fresh nested payload directories must not collide with their own prepared identity records.");
            }
        }
        string raceRoot=Path.Combine(fixture,"foreign-directory-race","app"),raceStage=raceRoot+"-stage";
        UpdateTransactionTests.Payload(raceStage,"0.1.1-beta.7",new(){{"DanmuCompanion.exe","main"},{"licenses/component/notice.txt","notice"}});
        Directory.CreateDirectory(raceRoot);string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(raceRoot),"direct-update");
        var authority=new UpdateMetadataAuthority(raceRoot,directory,true);
        using var dirs=new UpdateDirectories(authority.RecordDirectory);
        string foreign=Path.Combine(raceRoot,"licenses");string foreignId="";
        dirs.BeforeCreate=path=>{if(path.Equals(foreign,StringComparison.OrdinalIgnoreCase)){Directory.CreateDirectory(path);using var held=new UpdateDirectories();foreignId=held.Identity(path);}};
        UpdatePackageTests.Reject(()=>authority.PrepareDirectories(dirs,UpdatePackage.Read(raceStage)),"An external atomic-create winner must never become updater-owned.");
        DistributionTests.Check(foreignId!="" && !authority.CreatedDirectories().Any(record=>record.Path=="licenses") && Directory.Exists(foreign),"A same-name foreign directory created at the boundary must survive without being registered.");
    }
    private static void ReadSharedLocks(string fixture)
    {
        foreach(bool retired in new[]{false,true})foreach(bool repair in new[]{false,true})
        {
            string root=Path.Combine(fixture,"read-share-"+retired+"-"+repair,"app"),stage=root+"-stage";
            UpdateTransactionTests.Payload(root,"0.1.1-beta.6",new(){{"DanmuCompanion.exe","old"},{"stable.dll","old library"}});
            var incoming=new Dictionary<string,string>{{"DanmuCompanion.exe","new"}};if(!retired)incoming.Add("stable.dll","new library");
            UpdateTransactionTests.Payload(stage,"0.1.1-beta.7",incoming);
            string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update");var apply=UpdateApply.PrepareInstall(root,directory,stage);
            using var held=new FileStream(Path.Combine(root,"stable.dll"),FileMode.Open,FileAccess.Read,FileShare.Read);
            UpdatePackageTests.Reject(()=>{if(repair)UpdateApply.Load(directory).RepairCurrent();else apply.Apply();},"Existing readable deny-delete locks must fail preflight, including old-only resources and repair.");
            DistributionTests.Check(File.ReadAllText(Path.Combine(root,"DanmuCompanion.exe"))=="old" && File.ReadAllText(Path.Combine(root,"stable.dll"))=="old library","A pre-existing readable lock must not cause avoidable partial overwrite.");
        }
    }
    private static void DurableDirectoryRepair(string fixture)
    {
        foreach(bool foreign in new[]{false,true})
        {
            string root=Path.Combine(fixture,"directory-proof-repair-"+foreign,"app"),stage=root+"-stage";
            UpdateTransactionTests.Payload(root,"0.1.1-beta.6",new(){{"DanmuCompanion.exe","old"}});
            UpdateTransactionTests.Payload(stage,"0.1.1-beta.7",new(){{"DanmuCompanion.exe","new"},{"licenses/component/notice.txt","notice"}});
            string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update");var apply=UpdateApply.PrepareInstall(root,directory,stage);
            UpdatePackageTests.Reject(()=>apply.Apply(count=>{if(count==1)throw new IOException("directory proof interruption");}),"The directory repair fixture must stop after a partial payload commit.");
            string child=Path.Combine(root,"licenses/component");
            using(var dirs=new UpdateDirectories())DistributionTests.Check(new UpdateMetadataAuthority(root,directory,false).CreatedDirectories().Any(record=>record.Path=="licenses/component" && record.Identity==dirs.Identity(child)),"Created directory authority must survive operation reload before any nested payload write.");
            if(foreign)
            {
                Directory.Move(child,child+"-saved");Directory.CreateDirectory(child);string id;using(var dirs=new UpdateDirectories())id=dirs.Identity(child);
                UpdatePackageTests.Reject(()=>UpdateApply.Load(directory).RepairCurrent(),"Interrupted repair must not recapture a replaced same-name directory.");
                using var held=new UpdateDirectories();DistributionTests.Check(held.Identity(child)==id && !File.Exists(Path.Combine(child,"notice.txt")),"An empty foreign directory must survive repair without receiving application files.");
            }
            else
            {
                UpdateApply.Load(directory).RepairCurrent();UpdateRegistration.RequireReady(root,UpdatePackage.Read(root));
                DistributionTests.Check(UpdateRegistration.OwnedDirectories(root).Any(record=>record.Path=="licenses/component"),"Successful repair must retain durable created-directory ownership for later uninstall.");
            }
        }
    }
    private static void DirectoryCrashProofs(string fixture)
    {
        foreach(bool afterProof in new[]{false,true})
        {
            string root=Path.Combine(fixture,"directory-kill-"+afterProof,"app"),stage=root+"-stage",ready=root+"-ready";
            UpdateTransactionTests.Payload(root,"0.1.1-beta.6",new(){{"DanmuCompanion.exe","old"}});
            UpdateTransactionTests.Payload(stage,"0.1.1-beta.7",new(){{"DanmuCompanion.exe","new"},{"licenses/component/notice.txt","notice"}});
            string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update");UpdateApply.PrepareInstall(root,directory,stage);
            DistributionTests.Check(!Directory.Exists(Path.Combine(root,"licenses")),"Prepared updates must not mutate payload directories before journal arming.");
            var info=new ProcessStartInfo(Environment.ProcessPath){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
            foreach(string value in new[]{"--installer-update-fixture","kill-directory",root,directory,ready,afterProof ? "after-proof" : "before-proof"})info.ArgumentList.Add(value);
            using var child=Process.Start(info);long started=child.StartTime.ToUniversalTime().Ticks;child.WaitForExit();DistributionTests.Check(child.ExitCode==23 && File.Exists(ready),"A real child must exit at the directory creation/proof boundary.");
            var paths=new ApplicationPaths(root,"crash",Path.Combine(fixture,"crash-data-"+afterProof));string cache=Path.Combine(paths.ProfileRoot,"updates"),operation=UpdateStorage.CreateOperation(cache,"download"),id=Path.GetFileName(operation);
            var job=new UpdateJob(root,paths.DataRoot,"crash",null,false,id);
            UpdateStorage.Atomic(Path.Combine(operation,"worker.json"),JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=2,kind="direct-overwrite",job,applyDirectory=directory,workerPid=child.Id,workerStartTicks=started},UpdateStorage.Json));
            UpdateMaintenance.TryCleanupOperation(paths,operation);
            DistributionTests.Check(Directory.Exists(directory) && Directory.Exists(operation),"Old-host maintenance must retain an applying operation and its only creation proofs after worker death.");
            if(afterProof)
            {
                UpdateApply.Load(directory).RepairCurrent();UpdateRegistration.RequireReady(root,UpdatePackage.Read(root));
                DistributionTests.Check(File.ReadAllText(Path.Combine(root,"licenses/component/notice.txt"))=="notice","A killed creation with durable ID proof must repair without re-owning unknown objects.");
            }
            else
            {
                UpdatePackageTests.Reject(()=>UpdateApply.Load(directory).RepairCurrent(),"A creation killed before proof durability must fail closed rather than re-owning the directory.");
                using var held=new UpdateDirectories();DistributionTests.Check(held.Identity(Path.Combine(root,"licenses"))==File.ReadAllText(ready) && File.ReadAllText(Path.Combine(root,"DanmuCompanion.exe"))=="old","An unproven residual directory must remain intact and stop payload writes; manual new-location repair is required.");
            }
        }
        string retryRoot=Path.Combine(fixture,"directory-zero-write-retry","app"),retryStage=retryRoot+"-stage";
        UpdateTransactionTests.Payload(retryRoot,"0.1.1-beta.6",new(){{"DanmuCompanion.exe","old"}});
        UpdateTransactionTests.Payload(retryStage,"0.1.1-beta.7",new(){{"DanmuCompanion.exe","new"},{"licenses/A/notice.txt","a"},{"licenses/B/notice.txt","b"}});
        string failed=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(retryRoot),"direct-update");UpdateApply.PrepareInstall(retryRoot,failed,retryStage);
        var authority=new UpdateMetadataAuthority(retryRoot,failed,false);string foreign=Path.Combine(retryRoot,"licenses","B");
        using(var dirs=new UpdateDirectories(authority.RecordDirectory))
        {
            dirs.BeforeCreate=path=>{if(path.Equals(foreign,StringComparison.OrdinalIgnoreCase))Directory.CreateDirectory(path);};
            UpdatePackageTests.Reject(()=>authority.PrepareDirectories(dirs,UpdatePackage.Read(retryStage)),"An A-created/B-foreign failure must be reproducible before any payload write.");
        }
        DistributionTests.Check(!UpdateApply.CleanupUnapplied(retryRoot,failed) && !Directory.Exists(Path.Combine(retryRoot,"licenses/A")) && Directory.Exists(foreign) && Directory.Exists(failed),"Zero-write cleanup must retire only proven empty A, preserve foreign B and retain the blocked owned-parent proof.");
        // The still-owned parent contains foreign B, so keep its proof until the conflict is moved.
        Directory.Move(foreign,foreign+"-saved");Directory.Move(foreign+"-saved",retryRoot+"-foreign-saved");
        DistributionTests.Check(UpdateApply.CleanupUnapplied(retryRoot,failed),"A deferred parent cleanup must safely finish after the foreign child is preserved elsewhere.");
        UpdateStorage.RemoveOwned(UpdateInstalledOwnership.ParentFor(retryRoot),failed);
        string next=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(retryRoot),"direct-update");UpdateApply.PrepareInstall(retryRoot,next,retryStage).Apply();
        DistributionTests.Check(File.ReadAllText(Path.Combine(retryRoot,"licenses/A/notice.txt"))=="a","After proof-safe cleanup a new update attempt must not misclassify its own former empty directories as foreign.");
    }
    private static void CleanupQueryFailure(string fixture)
    {
        string root=Path.Combine(fixture,"cleanup-parent-query-denied","app"),stage=root+"-stage";
        UpdateTransactionTests.Payload(root,"0.1.1-beta.6",new(){{"DanmuCompanion.exe","old"},{"licenses/old/notice.txt","old notice"}});
        UpdateTransactionTests.Payload(stage,"0.1.1-beta.7",new(){{"DanmuCompanion.exe","new"},{"licenses/old/notice.txt","old notice"},{"licenses/new/notice.txt","new notice"}});
        string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update");UpdateApply.PrepareInstall(root,directory,stage);
        var authority=new UpdateMetadataAuthority(root,directory,false);using(var dirs=new UpdateDirectories(authority.RecordDirectory))authority.PrepareDirectories(dirs,UpdatePackage.Read(stage));
        var parent=new DirectoryInfo(Path.Combine(root,"licenses"));var acl=parent.GetAccessControl(AccessControlSections.Access);string saved=acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        using var user=WindowsIdentity.GetCurrent();
        try
        {
            acl.AddAccessRule(new(user.User,FileSystemRights.ReadAttributes|FileSystemRights.ListDirectory,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Deny));parent.SetAccessControl(acl);
            DistributionTests.Check(!UpdateApply.CleanupUnapplied(root,directory) && Directory.Exists(directory),"An inaccessible parent must not be treated as a missing child or permit deletion of the only directory proof.");
        }
        finally{acl.SetSecurityDescriptorSddlForm(saved,AccessControlSections.Access);parent.SetAccessControl(acl);}
        DistributionTests.Check(Directory.Exists(Path.Combine(root,"licenses","new")) && authority.CreatedDirectories().Any(record=>record.Path=="licenses/new"),"Restoring query permission must reveal both the original created directory and retained proof.");
        DistributionTests.Check(UpdateApply.CleanupUnapplied(root,directory) && !Directory.Exists(Path.Combine(root,"licenses","new")),"Proof-safe cleanup must resume after the real query denial is released.");
    }
    private static void CleanupOriginAndRoot(string fixture)
    {
        string root=Path.Combine(fixture,"cleanup-missing-origin","app"),stage=root+"-stage";
        UpdateTransactionTests.Payload(root,"0.1.1-beta.6",new(){{"DanmuCompanion.exe","old"}});
        UpdateTransactionTests.Payload(stage,"0.1.1-beta.7",new(){{"DanmuCompanion.exe","new"},{"licenses/new/notice.txt","notice"}});
        string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update");UpdateApply.PrepareInstall(root,directory,stage);
        var authority=new UpdateMetadataAuthority(root,directory,false);using(var dirs=new UpdateDirectories(authority.RecordDirectory))authority.PrepareDirectories(dirs,UpdatePackage.Read(stage));
        string origin=Path.Combine(directory,"metadata-original.json");File.Move(origin,origin+".saved");
        DistributionTests.Check(!UpdateApply.CleanupUnapplied(root,directory) && Directory.GetFiles(directory,"directory-*.json").Length==2,"Missing original root proof must never authorize removal of existing directory creation proofs.");
        File.Move(origin+".saved",origin);string moved=root+"-moved";Directory.Move(root,moved);
        DistributionTests.Check(!UpdateApply.CleanupUnapplied(root,directory) && !Directory.Exists(root),"Loading cleanup authority must not recreate a moved or missing original root.");
        Directory.Move(moved,root);bool denied=false;
        authority.CleanupRootHeld=()=>{try{Directory.Move(root,moved);}catch(IOException){denied=true;}};
        DistributionTests.Check(authority.CleanupCreatedDirectories() && denied && Directory.Exists(root) && !Directory.Exists(moved),"The original root object must remain pinned across every cleanup query and deletion, not only its initial identity check.");
    }
    private static void PortableRegistration(string fixture)
    {
        foreach(string mode in new[]{"success","interrupted","first-write-failure","sidecar-write-failure","first-temp","missing-sidecar","incomplete-sidecar","cloned-sidecar","cloned-marker","relocated","concurrent"})
        {
            string root=Path.Combine(fixture,"portable-"+mode,"app");UpdateTransactionTests.Payload(root,"0.1.1-beta.7",new(){{"DanmuCompanion.exe","main"},{"DanmuCompanion.Updater.exe","updater"},{"stable.dll","same bytes"}});
            var package=UpdatePackage.Read(root);string stable=Path.Combine(root,"stable.dll"),originalId=UpdateFileIdentity.Read(stable);
            if(mode=="first-write-failure")
            {
                string tempId="";UpdatePackageTests.Reject(()=>UpdateRegistration.InitializePortable(root,beforeFirstWrite:output=>{tempId=UpdateFileIdentity.Read(output);output.WriteByte(1);output.Flush(true);throw new IOException("first marker partial write");}),"A partial first marker write must leave stop evidence.");
                string temporary=Directory.GetFiles(root,".DanmuCompanion-registration-*.tmp").Single();
                DistributionTests.Check(UpdateFileIdentity.Read(temporary)==tempId && File.ReadAllBytes(temporary).SequenceEqual(new byte[]{1}),"The original partial marker object must remain after publication failure.");
                UpdatePackageTests.Reject(()=>UpdateRegistration.InitializePortable(root),"A first write failure must not silently establish a different ownership baseline on restart.");continue;
            }
            if(mode=="first-temp")
            {
                string temporary=Path.Combine(root,".DanmuCompanion-registration-"+Guid.NewGuid().ToString("N")+".tmp");File.WriteAllText(temporary,"interrupted first marker fixture");
                UpdatePackageTests.Reject(()=>UpdateRegistration.InitializePortable(root),"A first marker temporary left by a crash must stop re-bootstrap.");
                DistributionTests.Check(File.ReadAllText(temporary)=="interrupted first marker fixture" && !File.Exists(UpdateRegistration.PathFor(root)),"Unfinished or foreign initial temporary files must be retained, not ignored or deleted.");continue;
            }
            if(mode=="sidecar-write-failure")
            {
                string begunId="";UpdatePackageTests.Reject(()=>UpdateRegistration.InitializePortable(root,()=>{begunId=UpdateFileIdentity.Read(UpdateRegistration.PathFor(root));Directory.CreateDirectory(UpdateInstalledOwnership.PathFor(root));}),"Initial sidecar failure must not remove the only begun marker.");
                DistributionTests.Check(UpdateFileIdentity.Read(UpdateRegistration.PathFor(root))==begunId && UpdateFileIdentity.Read(stable)==originalId,"Repeated initializing writes must not replace the durable first-use evidence before a sidecar exists.");
                UpdatePackageTests.Reject(()=>UpdateRegistration.InitializePortable(root),"A failed sidecar publication must not retry first-use ownership.");continue;
            }
            if(mode=="interrupted")
            {
                UpdatePackageTests.Reject(()=>UpdateRegistration.InitializePortable(root,()=>throw new IOException("after durable begun")),"Interrupted first registration must leave a durable begun marker.");
                string markerId=UpdateFileIdentity.Read(UpdateRegistration.PathFor(root));
                UpdatePackageTests.Reject(()=>UpdateRegistration.InitializePortable(root),"An interrupted registration must not re-bootstrap current IDs.");
                DistributionTests.Check(UpdateFileIdentity.Read(UpdateRegistration.PathFor(root))==markerId && !File.Exists(UpdateInstalledOwnership.PathFor(root)),"A failed initializer must preserve its durable stop marker and not invent ownership.");continue;
            }
            if(mode=="concurrent")
            {
                using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
                var first=Task.Run(()=>UpdateRegistration.InitializePortable(root,()=>{entered.Set();release.Wait();}));
                try
                {
                    DistributionTests.Check(entered.Wait(TimeSpan.FromSeconds(3)),"The first initializer must reach its durable guarded boundary.");
                    UpdatePackageTests.Reject(()=>UpdateRegistration.InitializePortable(root),"Two first-use instances must not establish competing baselines.");
                    bool denied=false;try{Directory.Move(root,root+"-outside");}catch(IOException){denied=true;}
                    DistributionTests.Check(denied && Directory.Exists(root),"Initial registration must pin the real root through ownership publication.");
                }
                finally{release.Set();first.GetAwaiter().GetResult();}
                UpdateRegistration.RequireReady(root,package);continue;
            }
            UpdateRegistration.InitializePortable(root);UpdateRegistration.RequireReady(root,package);
            DistributionTests.Check(UpdateInstalledOwnership.Load(root).Length==package.Owned.Length && UpdateFileIdentity.Read(stable)==originalId,"First registration must include manifest and every unchanged original object without writing payload bytes.");
            string sidecar=UpdateInstalledOwnership.PathFor(root),marker=UpdateRegistration.PathFor(root);
            if(mode=="success")
            {
                string sidecarId=UpdateFileIdentity.Read(sidecar);UpdateRegistration.InitializePortable(root);
                DistributionTests.Check(UpdateFileIdentity.Read(sidecar)==sidecarId,"Repeated startup must not re-own or rewrite a registered baseline.");continue;
            }
            if(mode=="missing-sidecar")File.Move(sidecar,sidecar+".saved");
            if(mode=="incomplete-sidecar")File.WriteAllLines(sidecar,File.ReadAllLines(sidecar).Where(line=>!line.StartsWith("stable.dll|",StringComparison.Ordinal)));
            if(mode is "cloned-sidecar" or "cloned-marker")
            {string target=mode=="cloned-sidecar" ? sidecar : marker;File.Move(target,target+".saved");File.Copy(target+".saved",target);}
            if(mode=="relocated"){string moved=root+"-moved";Directory.Move(root,moved);root=moved;stable=Path.Combine(root,"stable.dll");}
            UpdatePackageTests.Reject(()=>UpdateRegistration.InitializePortable(root),"Missing, incomplete, cloned or relocated registration must not bootstrap again: "+mode);
            DistributionTests.Check(UpdateFileIdentity.Read(stable)==originalId,"Rejected initial registration must never alter payload objects.");
            var paths=new ApplicationPaths(root,"unit",Path.Combine(fixture,"portable-data-"+mode));
            using var unavailable=new UpdateService(paths);
            DistributionTests.Check(!unavailable.View.Supported && unavailable.View.State=="unavailable" && unavailable.View.Message.Contains("登记"),"Registration failure must disable updates with repair guidance, not the rest of the host UI.");
        }
        string fresh=Path.Combine(fixture,"portable-truly-fresh","app");UpdateTransactionTests.Payload(fresh,"0.1.1-beta.7",new(){{"DanmuCompanion.exe","main"},{"DanmuCompanion.Updater.exe","updater"}});
        UpdateRegistration.InitializePortable(fresh);UpdateRegistration.RequireReady(fresh,UpdatePackage.Read(fresh));
        DistributionTests.Check(UpdateInstalledOwnership.Load(fresh).Length==3,"A genuinely new empty-directory extraction can establish its own first-use baseline.");
    }
    private static void MetadataRepairAuthority(string fixture)
    {
        foreach(string mode in new[]{"cloned-sidecar","changed-sidecar","cloned-marker","sidecar-promote-gap","bound-rename"})
        {
            string root=Path.Combine(fixture,"metadata-"+mode,"app"),stage=root+"-stage";
            UpdateTransactionTests.Payload(root,"0.1.1-beta.7",new(){{"DanmuCompanion.exe","old"},{"stable.dll","old library"}});
            UpdateTransactionTests.Payload(stage,"0.1.1-beta.8",new(){{"DanmuCompanion.exe","new"},{"stable.dll","new library"}});
            var old=UpdatePackage.Read(root);UpdateInstalledOwnership.Save(root,old.Owned.Select(file=>new UpdateOwnedFile(file.Path,UpdateFileIdentity.Read(UpdatePackage.SafeFile(root,file.Path)))).ToArray());
            string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update");var apply=UpdateApply.PrepareInstall(root,directory,stage);
            if(mode=="bound-rename")
            {
                var field=typeof(UpdateApply).GetField("metadata",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                var authority=(UpdateMetadataAuthority)field.GetValue(apply);int denied=0;
                authority.BeforeRename=input=>{try{File.Move(input.Name,input.Name+"-foreign");}catch(IOException){denied++;}};
                apply.Apply();UpdateRegistration.RequireReady(root,UpdatePackage.Read(root));
                DistributionTests.Check(denied==3,"Both marker publications and sidecar promotion must retain the same deny-delete stream until bound rename.");continue;
            }
            if(mode=="sidecar-promote-gap")
            {
                var field=typeof(UpdateApply).GetField("metadata",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                var authority=(UpdateMetadataAuthority)field.GetValue(apply);FileStream lockProof=null;
                authority.AfterCommit=key=>{if(key=="sidecar")lockProof=new FileStream(Path.Combine(directory,"metadata-sidecar.current.json"),FileMode.Open,FileAccess.Read,FileShare.None);};
                try{UpdatePackageTests.Reject(()=>apply.Apply(),"A committed sidecar interrupted before proof promotion must retain pending authority.");}
                finally{lockProof?.Dispose();}
                var repaired=UpdateApply.Load(directory);repaired.RepairCurrent();repaired.AcceptInstalled();UpdateRegistration.RequireReady(root,UpdatePackage.Read(root));
                DistributionTests.Check(File.ReadAllText(Path.Combine(root,"DanmuCompanion.exe"))=="new","Metadata proof interruption must remain repairable without old bytes or blind identity re-capture.");continue;
            }
            UpdatePackageTests.Reject(()=>apply.Apply(count=>{if(count==1)throw new IOException("partial payload");}),"The metadata authority fixture must retain an interrupted operation.");
            string target=mode=="cloned-marker" ? UpdateRegistration.PathFor(root) : UpdateInstalledOwnership.PathFor(root);
            if(mode=="changed-sidecar")File.AppendAllText(target,"\r\n");else{File.Move(target,target+".saved");File.Copy(target+".saved",target);}
            string foreignId=UpdateFileIdentity.Read(target),before=File.ReadAllText(target),mainId=UpdateFileIdentity.Read(Path.Combine(root,"DanmuCompanion.exe"));
            UpdatePackageTests.Reject(()=>UpdateApply.Load(directory).RepairCurrent(),"Repair must use durable original/current/pending metadata proof, not current path identity: "+mode);
            DistributionTests.Check(UpdateFileIdentity.Read(target)==foreignId && File.ReadAllText(target)==before && UpdateFileIdentity.Read(Path.Combine(root,"DanmuCompanion.exe"))==mainId,"A foreign or edited metadata object must survive failed repair without any further payload mutation.");
        }
    }
    private static void ReadonlyAndDirectoryConflicts(string fixture)
    {
        foreach(bool readonlyFile in new[]{false,true})
        {
            string root=Path.Combine(fixture,"early-conflict-"+readonlyFile,"app"),stage=root+"-stage";
            UpdateTransactionTests.Payload(root,"0.1.1-beta.6",new(){{"DanmuCompanion.exe","old"},{"stable.dll","old library"}});UpdateTransactionTests.Payload(stage,"0.1.1-beta.7",new(){{"DanmuCompanion.exe","new"},{"stable.dll","new library"}});
            string library=Path.Combine(root,"stable.dll"),id=UpdateFileIdentity.Read(library);
            if(readonlyFile)File.SetAttributes(library,FileAttributes.ReadOnly);else Directory.CreateDirectory(UpdateInstalledOwnership.PathFor(root));
            try
            {
                UpdatePackageTests.Reject(()=>{string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update");UpdateApply.PrepareInstall(root,directory,stage).Apply();},"Readonly payload or an external sidecar directory must fail before any payload overwrite.");
                DistributionTests.Check(File.ReadAllText(Path.Combine(root,"DanmuCompanion.exe"))=="old" && UpdateFileIdentity.Read(library)==id,"Predictable deletion/metadata conflicts must be zero-payload-write failures.");
            }
            finally{if(readonlyFile)File.SetAttributes(library,FileAttributes.Normal);}
        }
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
