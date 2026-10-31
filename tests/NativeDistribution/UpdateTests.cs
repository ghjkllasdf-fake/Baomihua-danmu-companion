using System;
using System.Text.Json;

namespace Popcorn;

internal static class UpdateTests
{
    internal static void Run()
    {
        void Check(bool value,string message)=>DistributionTests.Check(value,message);
        Check(UpdateVersion.Newer("v0.1.1-beta.10","0.1.1-beta.9"),"Numeric beta identifiers must not sort lexically.");
        Check(UpdateVersion.Newer("0.1.1","0.1.1-beta.99"),"Final must follow its prerelease.");
        Check(!UpdateVersion.Newer("0.1.1+build.2","0.1.1+build.1"),"Build metadata is not an update.");
        foreach(string bad in new[]{"dev","1.2","1.02.3","1.2.3-beta.01","1.2.3-","1.2.3+x?","1.2.3\r\n"})
            Check(!UpdateVersion.TryParse(bad,out _),"Malformed version accepted: "+bad);
        Check(UpdateVersion.Newer("1.2.3-beta.999999999999999999999999","1.2.3-beta.123456789012345678901234"),"Numeric prereleases must not overflow.");
        string Feed(string version,bool draft=false,bool include=false,bool complete=true)=>JsonSerializer.Serialize(new[]{new{
            tag_name="v"+version,draft,prerelease=version.Contains('-'),body="Release notes",
            assets=new[]{new{name=UpdateCatalog.ArtifactName(version,include,"portable"),size=100,browser_download_url=$"https://github.com/{UpdateCatalog.Repository}/releases/download/v{version}/"+UpdateCatalog.ArtifactName(version,include,"portable"),digest=(string)null},
                new{name=complete ? "SHA256SUMS.txt" : "missing.txt",size=100,browser_download_url=$"https://github.com/{UpdateCatalog.Repository}/releases/download/v{version}/SHA256SUMS.txt",digest=(string)null}}}});
        Check(UpdateCatalog.Select(Feed("0.1.1-beta.10"),"0.1.1-beta.9","beta",false,"portable")?.Version=="0.1.1-beta.10","Beta release must be detected without latest-stable endpoint.");
        Check(UpdateCatalog.Select(Feed("0.1.1-beta.10"),"0.1.1-beta.9","stable",false,"portable")==null,"Stable channel must exclude betas.");
        Check(UpdateCatalog.Select(Feed("0.1.1-beta.10",draft:true),"0.1.1-beta.9","beta",false,"portable")==null,"Draft must never be an update.");
        Check(UpdateCatalog.Select(Feed("0.1.1-beta.10",include:true),"0.1.1-beta.9","beta",false,"portable")==null,"Edition must never be changed silently.");
        Check(UpdateCatalog.Select(Feed("0.1.1-beta.10",complete:false),"0.1.1-beta.9","beta",false,"portable")==null,"Release without checksums must not be selected.");
        Check(UpdateCatalog.Select(Feed("0.1.1-beta.9"),"0.1.1-beta.10","beta",false,"portable")==null,"Downgrade must be refused.");
        Check(UpdateCatalog.Select(Feed("0.1.2-preview.1"),"0.1.1-beta.9","beta",false,"portable")==null,"Preview must not leak into beta channel.");
        string name=UpdateCatalog.ArtifactName("0.1.1-beta.10",true,"installer");
        Check(name=="DanmuCompanion-0.1.1-beta.10-win-x64-includeAPI-setup.exe","Installer naming must preserve includeAPI.");
        Check(UpdateCatalog.InventoryName("0.1.1-beta.10",true)=="DanmuCompanion-0.1.1-beta.10-win-x64-includeAPI-manifest.json","Incoming installed ownership must use the edition-specific release inventory.");
        string hash=new string('a',64);
        Check(UpdateCatalog.Checksum(hash+"  "+name+"\n",name)==hash,"Expected checksum must be selected by exact filename.");
        foreach(string text in new[]{hash+"  other.exe",hash+"  "+name+"\n"+hash+"  "+name,"wrong  "+name})
        {
            bool rejected=false;try{UpdateCatalog.Checksum(text,name);}catch(System.IO.InvalidDataException){rejected=true;}
            Check(rejected,"Missing, duplicate or malformed checksum was accepted.");
        }
        Console.WriteLine("PASS: update version, channel, edition and checksum contracts.");
        UpdatePackageTests.Run();
        UpdateTransportTests.Run().GetAwaiter().GetResult();
        UpdateServiceTests.Run().GetAwaiter().GetResult();
        UpdateTransactionTests.Run();
        UpdateInstallerTests.Run();
        UpdatePipeTests.Run().GetAwaiter().GetResult();
        UpdateMaintenanceTests.Run();
    }
}
