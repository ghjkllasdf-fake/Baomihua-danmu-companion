using System;
using System.Collections.Generic;
using System.IO;

namespace Popcorn;

internal static class UpdateInstallerTests
{
    private sealed class Registration : IUpdateRegistration
    {
        public bool Restored;
        public void Save(string directory)=>File.WriteAllText(Path.Combine(directory,"registration.json"),"controlled registry fixture");
        public void Restore()=>Restored=true;
    }
    internal static int Fixture(string[] args)
    {
        if(args.Length==5 && args[1]=="prepare")
        {
            var next=UpdatePackage.Read(args[3]);UpdateInstallerTransaction.Prepare(args[2],args[4],next.Version,next.Variant,new Registration(),next);
            string stage=Path.Combine(args[4],"stage");System.IO.Directory.CreateDirectory(stage);
            foreach(var file in next.Owned){string target=UpdatePackage.SafeFile(stage,file.Path);System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(UpdatePackage.SafeFile(args[3],file.Path),target);}return 0;
        }
        if(args.Length==3 && args[1]=="create-new"){var transaction=UpdateInstallerTransaction.Load(args[2],new Registration());transaction.PreflightApply();transaction.CreateNewFiles();return 0;}
        if(args.Length==3 && args[1]=="restore"){UpdateInstallerTransaction.Load(args[2],new Registration()).Restore();return 0;}
        if(args.Length==3 && args[1]=="accept"){UpdateInstallerTransaction.Load(args[2],new Registration()).AcceptInstalled();return 0;}
        throw new ArgumentException("Invalid installer fixture options.");
    }
    internal static void Run()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"DanmuUpdateInstaller-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        try
        {
            string deep=Path.Combine(fixture,new string('a',90),new string('b',90),new string('c',90),"owned.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(deep));File.WriteAllText(deep,"owned long-path bytes");
            DistributionTests.Check(UpdateFileIdentity.RemoveMatching(deep,UpdateFileIdentity.Read(deep)) && !File.Exists(deep),"Owned cleanup must support long nested payload paths without changing Windows policy.");
            string root=Path.Combine(fixture,"application"),parent=UpdateTransaction.ParentFor(root);
            UpdateTransactionTests.Payload(root,"0.1.1-beta.9",new Dictionary<string,string>{{"DanmuCompanion.exe","old controlled fixture"}});
            File.WriteAllText(Path.Combine(root,"unins000.exe"),"old owned uninstall fixture");File.WriteAllText(Path.Combine(root,"user.txt"),"foreign");
            string directory=UpdateStorage.CreateOperation(parent,"transaction");var registry=new Registration();
            string stage=Path.Combine(fixture,"stage");UpdateTransactionTests.Payload(stage,"0.1.1-beta.10",new Dictionary<string,string>{{"DanmuCompanion.exe","new controlled fixture"},{"new.dll","new library"}});
            var incoming=UpdatePackage.Read(stage);
            var transaction=UpdateInstallerTransaction.Prepare(root,directory,"0.1.1-beta.10","standard",registry,incoming);
            UpdateTransactionTests.Payload(root,"0.1.1-beta.10",new Dictionary<string,string>{{"DanmuCompanion.exe","new controlled fixture"},{"new.dll","new library"}});
            transaction.RecordCreatedForTest("new.dll");
            File.WriteAllText(Path.Combine(root,"unins000.exe"),"new uninstall fixture");transaction.AcceptInstalled();transaction.Restore();
            UpdatePackage.VerifyDirectory(root,"0.1.1-beta.9","standard");
            string advancedRoot=Path.Combine(fixture,"newer-install"),advancedStage=Path.Combine(fixture,"newer-install-stage");
            UpdateTransactionTests.Payload(advancedRoot,"0.1.1-beta.9",new Dictionary<string,string>{{"DanmuCompanion.exe","old"}});
            UpdateTransactionTests.Payload(advancedStage,"0.1.1-beta.10",new Dictionary<string,string>{{"DanmuCompanion.exe","next"}});
            var advancedRegistration=new Registration();
            var obsoleteRecovery=UpdateInstallerTransaction.Prepare(advancedRoot,UpdateStorage.CreateOperation(UpdateTransaction.ParentFor(advancedRoot),"transaction"),"0.1.1-beta.10","standard",advancedRegistration,UpdatePackage.Read(advancedStage));
            UpdateTransactionTests.Payload(advancedRoot,"0.1.1-beta.11",new Dictionary<string,string>{{"DanmuCompanion.exe","newer successful install"}});
            using(var lockedManifest=new FileStream(Path.Combine(advancedRoot,"manifest.json"),FileMode.Open,FileAccess.Read,FileShare.None))
            {
                bool rejected=false;
                try{obsoleteRecovery.Restore();}catch(Exception error) when(error is IOException or UnauthorizedAccessException){rejected=true;}
                DistributionTests.Check(rejected,"An unreadable current manifest must block stale recovery before any mutation.");
                DistributionTests.Check(!advancedRegistration.Restored && File.ReadAllText(Path.Combine(advancedRoot,"DanmuCompanion.exe"))=="newer successful install","A locked newer manifest must leave all installed files and registration untouched.");
            }
            UpdatePackageTests.Reject(()=>obsoleteRecovery.Restore(),"An old installed transaction must not overwrite a later valid installation.");
            DistributionTests.Check(!advancedRegistration.Restored && File.ReadAllText(Path.Combine(advancedRoot,"DanmuCompanion.exe"))=="newer successful install","A stale recovery must reject before touching files or registration.");
            string dynamicRoot=Path.Combine(fixture,"dynamic-app"),dynamicStage=Path.Combine(fixture,"dynamic-stage");
            UpdateTransactionTests.Payload(dynamicRoot,"0.1.1-beta.9",new Dictionary<string,string>{{"DanmuCompanion.exe","old"},{"dynamic.dll","old dynamic"}});
            UpdateInstalledOwnership.Save(dynamicRoot,new[]{new UpdateOwnedFile("dynamic.dll",UpdateFileIdentity.Read(Path.Combine(dynamicRoot,"dynamic.dll")))});
            UpdateTransactionTests.Payload(dynamicStage,"0.1.1-beta.10",new Dictionary<string,string>{{"DanmuCompanion.exe","new"},{"dynamic.dll","new dynamic"},{"new.dll","new"}});
            string dynamicDirectory=UpdateStorage.CreateOperation(UpdateTransaction.ParentFor(dynamicRoot),"transaction");
            UpdateInstallerTransaction.Prepare(dynamicRoot,dynamicDirectory,"0.1.1-beta.10","standard",new Registration(),UpdatePackage.Read(dynamicStage));
            Directory.Move(dynamicStage,Path.Combine(dynamicDirectory,"stage"));
            var loaded=UpdateInstallerTransaction.Load(dynamicDirectory,new Registration());loaded.PreflightApply();loaded.CreateNewFiles();
            DistributionTests.Check(File.ReadAllText(Path.Combine(dynamicRoot,"dynamic.dll"))=="new dynamic","A subsequent update must replace previously native-owned bytes after loading its ownership snapshot.");
            DistributionTests.Check(!File.ReadAllText(Path.Combine(dynamicDirectory,"installer-originals.txt")).Contains("dynamic.dll"),"Inno must continue skipping native-owned paths on later updates.");
            loaded.Restore();UpdatePackage.VerifyDirectory(dynamicRoot,"0.1.1-beta.9","standard");
            DistributionTests.Check(UpdateInstalledOwnership.Load(dynamicRoot).Length==1,"Rollback must retain and refresh prior dynamic uninstall ownership.");
            foreach(string scenario in new[]{"accept-successor","rollback-successor","obsolete-successor","obsolete-locked"})
            {
                string app=Path.Combine(fixture,scenario),source=Path.Combine(fixture,scenario+"-stage");
                UpdateTransactionTests.Payload(app,"0.1.1-beta.9",new Dictionary<string,string>{{"DanmuCompanion.exe","old"},{"dynamic.dll","old dynamic"}});
                string originalIdentity=UpdateFileIdentity.Read(Path.Combine(app,"dynamic.dll"));
                UpdateInstalledOwnership.Save(app,new[]{new UpdateOwnedFile("dynamic.dll",originalIdentity)});
                var entries=new Dictionary<string,string>{{"DanmuCompanion.exe","new"}};
                if(!scenario.StartsWith("obsolete-")){entries.Add("dynamic.dll","new dynamic");entries.Add("new.dll","new library");}
                UpdateTransactionTests.Payload(source,"0.1.1-beta.10",entries);
                string journal=UpdateStorage.CreateOperation(UpdateTransaction.ParentFor(app),"transaction");
                var operation=UpdateInstallerTransaction.Prepare(app,journal,"0.1.1-beta.10","standard",new Registration(),UpdatePackage.Read(source));
                Directory.Move(source,Path.Combine(journal,"stage"));operation.PreflightApply();operation.CreateNewFiles();
                File.Copy(Path.Combine(journal,"stage","DanmuCompanion.exe"),Path.Combine(app,"DanmuCompanion.exe"),true);
                File.Copy(Path.Combine(journal,"stage","manifest.json"),Path.Combine(app,"manifest.json"),true);
                if(scenario=="obsolete-locked")
                {
                    bool rejected=false;using(var locked=File.Open(Path.Combine(app,"dynamic.dll"),FileMode.Open,FileAccess.Read,FileShare.Read))
                    {try{operation.AcceptInstalled();}catch(InvalidDataException){rejected=true;}}
                    DistributionTests.Check(rejected && UpdateInstalledOwnership.Load(app)[0].Identity==originalIdentity,"Locked obsolete owned files must retain recovery ownership, not be reported as foreign and abandoned.");
                    operation.Restore();continue;
                }
                string name=scenario=="accept-successor" ? "new.dll" : "dynamic.dll",path=Path.Combine(app,name);
                string content=scenario=="rollback-successor" ? "old dynamic" : File.ReadAllText(path);
                File.Move(path,path+".foreign-displaced");File.WriteAllText(path,content);
                if(scenario=="accept-successor")
                {
                    bool rejected=false;try{operation.AcceptInstalled();}catch(InvalidDataException){rejected=true;}
                    DistributionTests.Check(rejected,"Accept must reject an identical-byte successor of a native-created file.");
                    operation.Restore();DistributionTests.Check(File.ReadAllText(path)==content,"Rollback must preserve an unrecognized newly introduced successor.");
                }
                else if(scenario=="rollback-successor")
                {
                    operation.Restore();var saved=UpdateInstalledOwnership.Load(app);
                    DistributionTests.Check(saved[0].Identity==originalIdentity && saved[0].Identity!=UpdateFileIdentity.Read(path),"Rollback must not claim an identical-byte foreign successor as owned.");
                    bool rejected=false;try{UpdateInstallerTransaction.Prepare(app,UpdateStorage.CreateOperation(UpdateTransaction.ParentFor(app),"transaction"),"0.1.1-beta.10","standard",new Registration(),UpdatePackage.Read(Path.Combine(journal,"stage")));}catch(InvalidDataException){rejected=true;}
                    DistributionTests.Check(rejected,"A later update must refuse the foreign successor preserved during rollback.");
                }
                else
                {
                    operation.AcceptInstalled();DistributionTests.Check(File.Exists(path) && operation.RecoveryNotice.Length>0,"Obsolete native-owned paths replaced by same-byte foreign files must be preserved and reported.");
                }
            }
            DistributionTests.Check(registry.Restored,"Installer rollback must restore its own registration, not just EXE bytes.");
            DistributionTests.Check(File.ReadAllText(Path.Combine(root,"unins000.exe"))=="old owned uninstall fixture","Rollback must restore the original uninstall payload.");
            DistributionTests.Check(!File.Exists(Path.Combine(root,"new.dll")) && File.ReadAllText(Path.Combine(root,"user.txt"))=="foreign","Installer rollback must reconcile only authorized application files.");
            string failure=UpdateStorage.CreateOperation(parent,"transaction");var incomplete=UpdateInstallerTransaction.Prepare(root,failure,"0.1.1-beta.10","standard",new Registration(),incoming);
            File.WriteAllText(Path.Combine(root,"DanmuCompanion.exe"),"partially installed bytes");File.Copy(Path.Combine(stage,"new.dll"),Path.Combine(root,"new.dll"));
            incomplete.RecordCreatedForTest("new.dll");File.WriteAllText(Path.Combine(root,"new.dll"),"partial new library");
            incomplete.Restore();UpdatePackage.VerifyDirectory(root,"0.1.1-beta.9","standard");
            DistributionTests.Check(!File.Exists(Path.Combine(root,"new.dll")),"Failed setup must remove verified new files even before AcceptInstalled.");
            string lockedDirectory=UpdateStorage.CreateOperation(parent,"transaction");var lockedNew=UpdateInstallerTransaction.Prepare(root,lockedDirectory,"0.1.1-beta.10","standard",new Registration(),incoming);
            File.Copy(Path.Combine(stage,"new.dll"),Path.Combine(root,"new.dll"));lockedNew.RecordCreatedForTest("new.dll");
            using(var handle=File.Open(Path.Combine(root,"new.dll"),FileMode.Open,FileAccess.Read,FileShare.Read))
                UpdatePackageTests.Reject(()=>lockedNew.Restore(),"Locked created files must retain their recovery journal rather than silently abandon owned payload.");
            lockedNew.Restore();DistributionTests.Check(!File.Exists(Path.Combine(root,"new.dll")),"Created-file cleanup must be retryable after unlocking.");
            string raced=UpdateStorage.CreateOperation(parent,"transaction");var foreign=UpdateInstallerTransaction.Prepare(root,raced,"0.1.1-beta.10","standard",new Registration(),incoming);
            File.Copy(Path.Combine(stage,"new.dll"),Path.Combine(root,"new.dll"));foreign.Restore();
            DistributionTests.Check(File.ReadAllText(Path.Combine(root,"new.dll"))=="new library","An identical foreign collision without a creation journal must not be deleted.");
            File.Delete(Path.Combine(root,"new.dll"));
            string corruptDirectory=UpdateStorage.CreateOperation(parent,"transaction");var corruptRegistration=new Registration();
            var corrupt=UpdateInstallerTransaction.Prepare(root,corruptDirectory,"0.1.1-beta.10","standard",corruptRegistration,incoming);
            File.WriteAllText(Path.Combine(root,"DanmuCompanion.exe"),"partially installed main");File.WriteAllText(Path.Combine(root,"new.dll"),"unproven new bytes");
            File.WriteAllText(Path.Combine(root,"manifest.json"),"{interrupted manifest");
            File.WriteAllText(Path.Combine(corruptDirectory,"1.file-id"),"new.dll|partial");corrupt.Restore();
            DistributionTests.Check(corruptRegistration.Restored && corrupt.RecoveryNotice.Length>0 && File.ReadAllText(Path.Combine(root,"new.dll"))=="unproven new bytes","Corrupt creation logs must still restore originals and registry while preserving unproven bytes.");
            UpdatePackage.VerifyDirectory(root,"0.1.1-beta.9","standard");
        }
        finally
        {
            if(!Path.GetFullPath(fixture).StartsWith(Path.GetFullPath(Path.GetTempPath())+"DanmuUpdateInstaller-",StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe fixture cleanup root.");
            Directory.Delete(fixture,true);
        }
        Console.WriteLine("PASS: installed-payload rollback, uninstall payload, registration seam and foreign-file preservation.");
    }
}
