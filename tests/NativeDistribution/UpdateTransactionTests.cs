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
        string fixture=Path.Combine(Path.GetTempPath(),"DanmuUpdateDirect-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        try
        {
            foreach(string mode in new[]{"success","unchanged","unchanged-foreign","partial","repair-interrupted","repair-promote-gap","repair-old-modified","repair-foreign","locked","late-foreign","same-content-successor","stage-corrupt","delete-exe","delete-manifest","delete-list","metadata-conflict","root-rename"})
            {
                string baseRoot=Path.Combine(fixture,mode),root=Path.Combine(baseRoot,"app"),stage=Path.Combine(baseRoot,"stage");
                var old=new Dictionary<string,string>{{"DanmuCompanion.exe","original exe"},{"install-files.txt","original list"},{"old.dll","old library"}};
                var next=new Dictionary<string,string>{{"DanmuCompanion.exe","incoming exe"},{"install-files.txt","incoming list"},{"new.dll","new library"}};
                if(mode.StartsWith("unchanged",StringComparison.Ordinal)){old.Add("stable.dll","unchanged library");next.Add("stable.dll","unchanged library");}
                Payload(root,"0.1.1-beta.6",old);Payload(stage,"0.1.1-beta.7",next);File.WriteAllText(Path.Combine(root,"foreign.txt"),"foreign sentinel");
                string unchangedIdentity=mode=="unchanged" ? UpdateFileIdentity.Read(Path.Combine(root,"stable.dll")) : "";
                string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update");var apply=UpdateApply.Prepare(root,directory,stage,"0.1.1-beta.7","standard");
                DistributionTests.Check(!Directory.Exists(Path.Combine(directory,"backup")) && !Directory.GetFiles(directory,"*",SearchOption.AllDirectories).Any(f=>Path.GetFileName(f)=="DanmuCompanion.exe"),"Preparation must save metadata only, not copy or archive the original program.");
                DistributionTests.Check(typeof(UpdateApply).GetMethod("Restore")==null && typeof(UpdateApply).GetMethod("Recover")==null,"No old-version restore capability may remain.");
                if(mode=="success" || mode=="root-rename" || mode=="unchanged")
                {
                    bool denied=false;
                    apply.Apply(afterRemove:mode=="root-rename" ? _=>{try{Directory.Move(root,root+"-outside");}catch(IOException){denied=true;}DistributionTests.Check(Directory.Exists(root) && !Directory.Exists(root+"-outside"),"Held root must reject directory rename before file commits.");} : null);
                    if(mode=="root-rename")DistributionTests.Check(denied && !Directory.Exists(root+"-outside"),"Held root directories must block rename/reparse races before file commits.");
                    if(mode=="unchanged")DistributionTests.Check(UpdateFileIdentity.Read(Path.Combine(root,"stable.dll"))==unchangedIdentity,"Hash-identical owned files must keep their validated disk identity without pointless rewrites.");
                    UpdatePackage.VerifyDirectory(root,"0.1.1-beta.7","standard");
                    DistributionTests.Check(!File.Exists(Path.Combine(root,"old.dll")) && File.Exists(UpdateRepair.PathFor(directory)),"Retire only old owned files and retain repair metadata until health succeeds.");
                    apply.CompleteMetadata();DistributionTests.Check(!File.Exists(UpdateRepair.PathFor(directory)),"A healthy commit removes only metadata, never an old-program backup.");
                }
                else if(mode=="unchanged-foreign")
                {
                    string stable=Path.Combine(root,"stable.dll"),foreignId="";
                    UpdatePackageTests.Reject(()=>apply.Apply(count=>{if(count==1){File.Move(stable,stable+".saved");File.WriteAllText(stable,"unchanged library");foreignId=UpdateFileIdentity.Read(stable);}}),"Unchanged-byte optimization must reject a late same-byte foreign successor.");
                    DistributionTests.Check(apply.Mutated && UpdateFileIdentity.Read(stable)==foreignId && File.ReadAllText(stable)=="unchanged library","Late foreign stable file must not be overwritten or re-owned.");
                }
                else if(mode=="partial")
                {
                    UpdatePackageTests.Reject(()=>apply.Apply(count=>{if(count==1)throw new IOException("controlled interruption");}),"Injected overwrite interruption must fail.");
                    DistributionTests.Check(apply.Mutated && File.ReadAllText(Path.Combine(root,"DanmuCompanion.exe"))=="incoming exe","A partial update must never restore original bytes.");
                    foreach(var file in UpdatePackage.Read(stage).Owned)File.Copy(UpdatePackage.SafeFile(stage,file.Path),UpdatePackage.SafeFile(root,file.Path),true);
                    UpdatePackage.VerifyDirectory(root,"0.1.1-beta.7","standard");
                }
                else if(mode.StartsWith("repair-",StringComparison.Ordinal))
                {
                    UpdatePackageTests.Reject(()=>apply.Apply(count=>{if(count==1)throw new IOException("controlled interruption");}),"First partial update must retain repair authority.");
                    string target=Path.Combine(root,"DanmuCompanion.exe"),identity=UpdateFileIdentity.Read(target);
                    for(int retry=0;retry<3;retry++)
                    {
                        if(mode=="repair-promote-gap")
                        {
                            FileStream held=null;
                            try{UpdatePackageTests.Reject(()=>UpdateApply.Load(directory).RepairCurrent(path=>{if(path=="DanmuCompanion.exe")held=new FileStream(Path.Combine(directory,"1.file-id"),FileMode.Open,FileAccess.Read,FileShare.None);}),"Commit-to-proof-promotion interruption must retain pending authority.");}
                            finally{held?.Dispose();}
                            DistributionTests.Check(File.Exists(Path.Combine(directory,"1.pending.file-id")) && File.ReadAllText(target)=="incoming exe","A committed replacement must remain repairable via its pending identity.");
                        }
                        else
                        {
                            UpdatePackageTests.Reject(()=>UpdateApply.Load(directory).RepairCurrent(path=>{if(path=="DanmuCompanion.exe")throw new IOException("interrupted after proof");}),"Interrupted repair must retain current identity.");
                            DistributionTests.Check(UpdateFileIdentity.Read(target)==identity && File.ReadAllText(target)=="incoming exe","Persisting prospective identity must not lose authority for unchanged current bytes.");
                        }
                    }
                    if(mode=="repair-foreign")
                    {
                        File.Move(target,target+".foreign");File.WriteAllText(target,"incoming exe");string foreignIdentity=UpdateFileIdentity.Read(target);
                        UpdatePackageTests.Reject(()=>UpdateApply.Load(directory).RepairCurrent(),"Same-byte foreign successor must not be accepted by repeated repair.");
                        DistributionTests.Check(UpdateFileIdentity.Read(target)==foreignIdentity && File.ReadAllText(target)=="incoming exe","Foreign successor must remain untouched.");
                    }
                    else
                    {
                        if(mode=="repair-old-modified")File.WriteAllText(Path.Combine(root,"old.dll"),"user changed old-only library");
                        var repaired=UpdateApply.Load(directory);repaired.RepairCurrent();repaired.AcceptInstalled();
                        DistributionTests.Check(mode=="repair-old-modified" ? File.ReadAllText(Path.Combine(root,"old.dll"))=="user changed old-only library" : !File.Exists(Path.Combine(root,"old.dll")),"Repeated repair must retire only unchanged old-owned files.");
                    }
                }
                else if(mode.StartsWith("delete-",StringComparison.Ordinal))
                {
                    string deleted=mode=="delete-exe" ? "DanmuCompanion.exe" : mode=="delete-list" ? "install-files.txt" : "manifest.json";
                    UpdatePackageTests.Reject(()=>apply.Apply(afterRemove:path=>{if(path==deleted)throw new IOException("controlled delete/rename interruption");}),"Every delete/rename gap must fail truthfully.");
                    DistributionTests.Check(apply.Mutated && !File.Exists(Path.Combine(root,deleted)) && File.Exists(UpdateRepair.PathFor(directory)),"Missing exe/manifest/list must retain scoped repair metadata, not a backup.");
                }
                else
                {
                    if(mode=="metadata-conflict")File.WriteAllText(Path.Combine(directory,"repair.txt"),"foreign metadata");
                    if(mode=="late-foreign")File.WriteAllText(Path.Combine(root,"new.dll"),"foreign same path");
                    if(mode=="same-content-successor"){string path=Path.Combine(root,"DanmuCompanion.exe");File.Move(path,path+".saved");File.WriteAllText(path,"original exe");}
                    if(mode=="stage-corrupt")File.AppendAllText(Path.Combine(stage,"new.dll"),"modified");
                    if(mode=="locked")using(var held=new FileStream(Path.Combine(root,"old.dll"),FileMode.Open,FileAccess.Read,FileShare.None))UpdatePackageTests.Reject(()=>apply.Apply(),"All old locks must be checked before any overwrite.");
                    else UpdatePackageTests.Reject(()=>apply.Apply(),"Late foreign, changed identities and stage tampering must stop before overwrite.");
                    if(mode=="metadata-conflict")DistributionTests.Check(File.ReadAllText(Path.Combine(directory,"repair.txt"))=="foreign metadata","Foreign metadata must not be overwritten.");
                    DistributionTests.Check(!apply.Mutated && File.ReadAllText(Path.Combine(root,"DanmuCompanion.exe"))=="original exe","A zero-write failure must leave the old payload unchanged.");
                }
                DistributionTests.Check(File.ReadAllText(Path.Combine(root,"foreign.txt"))=="foreign sentinel","Direct update and repair must preserve foreign files.");
            }
        }
        finally{UpdatePackage.Check(Path.GetFullPath(fixture).StartsWith(Path.Combine(Path.GetTempPath(),"DanmuUpdateDirect-"),StringComparison.OrdinalIgnoreCase),"Unsafe fixture cleanup.");Directory.Delete(fixture,true);}
        Console.WriteLine("PASS: validated direct overwrite, no old-byte copies/rollback, foreign identities, locks, partial failure and missing-file repair scope.");
    }
}
