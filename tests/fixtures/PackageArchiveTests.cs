using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Popcorn.Release;

public static class PackageArchiveTests
{
    private static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    private static void Reject(Action action,string expected)
    {
        try { action(); }
        catch(Exception error)
        {
            Check(error.Message.Contains(expected,StringComparison.OrdinalIgnoreCase),"Wrong failure: "+error.GetType().Name+"; expected "+expected);
            return;
        }
        throw new Exception("Expected rejection: "+expected);
    }
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Rewrite(string zip,string path,string text)
    {
        using var archive=ZipFile.Open(zip,ZipArchiveMode.Update);
        archive.GetEntry(path)?.Delete();
        using var writer=new StreamWriter(archive.CreateEntry(path).Open(),new UTF8Encoding(false));
        writer.Write(text);
    }
    public static void Run(string workspace)
    {
        string source=Path.Combine(workspace,"source");
        var fixture=PackageArchive.RequiredPaths.Select(p=>PackageInput.Text(p,"synthetic fixture "+p)).ToList();
        fixture.Add(PackageInput.Text("ui/dist/assets/main-test.js","synthetic compiled script"));
        int next=0;
        string NewPath()=>Path.Combine(workspace,"test-"+(++next)+".zip");
        string Build(IEnumerable<PackageInput> inputs,string[] secrets=null)
        {
            string zip=NewPath();
            PackageArchive.Create(source,zip,"0.1.0-preview.1",inputs.ToArray(),secrets??Array.Empty<string>());
            return zip;
        }
        string original=Build(fixture);
        var result=PackageArchive.Verify(original);
        Check(result.Files==fixture.Count && result.Version=="0.1.0-preview.1","Synthetic archive did not roundtrip its manifest.");
        string second=Build(fixture);
        Check(Hash(original)==Hash(second),"Equivalent input must produce byte-identical ZIPs.");
        Reject(()=>PackageArchive.Create(source,original,"0.1.0-preview.1",fixture.ToArray(),Array.Empty<string>()),"already exists");
        Reject(()=>Build(fixture.Where(p=>p.Path!="desktop/native/Core.cs")),"required");
        foreach(string native in new[]{"AutomaticMatch","MatchFailureNotification","OverlayPresentation","PlaybackEventSignal"})
            Reject(()=>Build(fixture.Where(p=>p.Path!="desktop/native/"+native+".cs")),"required");
        Reject(()=>Build(fixture.Where(p=>p.Path!=".deps/webview2/runtimes/win-x64/native/WebView2Loader.dll")),"required");
        Console.WriteLine("PASS: synthetic manifest/hash roundtrip, deterministic ZIP bytes, no overwrite and required native/loader paths.");

        foreach(string path in new[] {"../leak.txt","/absolute.txt","C:/leak.txt","desktop/../leak.cs","desktop\\native\\leak.cs",
            "desktop/native/CON.cs","desktop/native/Core.cs:stream","desktop/native/Core.cs.","desktop/native/Core.cs ",
            ".deps/pwsh/bad?.txt",".deps/pwsh/CONIN$.txt",
            ".cache/app/preferences.json","DanmuCompanion.ps1","ui/node_modules/leak.js","ui/dist/assets/main.js.map"})
            Reject(()=>Build(fixture.Append(PackageInput.Text(path,"do not ship"))),"path");
        Reject(()=>Build(fixture.Append(PackageInput.Text("DESKTOP/native/Core.cs","collision"))),"duplicate");
        Reject(()=>PackageArchive.Create(source,NewPath(),"../bad",fixture.ToArray(),Array.Empty<string>()),"version");
        Console.WriteLine("PASS: traversal, Windows aliases/ADS, private files, source maps and case-colliding entries are rejected.");

        string code=Path.Combine(source,"extra.cs"); File.WriteAllText(code,"private-fixture-key-123");
        var contaminated=fixture.Where(p=>p.Path!="desktop/native/Core.cs")
            .Append(PackageInput.File("desktop/native/Core.cs",code));
        Reject(()=>Build(contaminated,new[]{"private-fixture-key-123"}),"sensitive");
        Reject(()=>Build(fixture.Append(PackageInput.File("desktop/native/Outside.cs",Path.Combine(workspace,"outside","source.cs")))),"outside");
        Reject(()=>Build(fixture.Append(PackageInput.File("desktop/native/Linked.cs",Path.Combine(source,"linked","source.cs")))),"reparse");
        Reject(()=>PackageArchive.Create(Path.Combine(source,"linked"),NewPath(),"0.1.0-preview.1",
            fixture.Append(PackageInput.File("desktop/native/Linked.cs",Path.Combine(source,"linked","source.cs"))).ToArray(),Array.Empty<string>()),"reparse");
        Console.WriteLine("PASS: known sensitive text, project-root escape and source junctions are rejected.");

        string dependency=Path.Combine(source,"dependency.zip");
        using(var archive=ZipFile.Open(dependency,ZipArchiveMode.Create))
        using(var writer=new StreamWriter(archive.CreateEntry("member.dll").Open())) writer.Write("synthetic dependency");
        Reject(()=>Build(fixture.Append(PackageInput.Zip(".deps/pwsh/extra.dll",dependency,"member.dll",new string('0',64)))),"checksum");
        string withZip=Build(fixture.Append(PackageInput.Zip(".deps/pwsh/extra.dll",dependency,"member.dll",Hash(dependency))));
        Check(PackageArchive.Verify(withZip).Files==fixture.Count+1,"Pinned archive member did not roundtrip.");
        Reject(()=>Build(fixture.Append(PackageInput.Zip(".deps/pwsh/first.dll",dependency,"member.dll",Hash(dependency)))
            .Append(PackageInput.Zip(".deps/pwsh/second.dll",dependency.ToUpperInvariant(),"member.dll",new string('0',64)))),"checksum");
        Console.WriteLine("PASS: dependency archives require their exact checksum before member copying.");

        string changed=NewPath(); File.Copy(original,changed);
        Rewrite(changed,"desktop/native/Core.cs",new string('x',Encoding.UTF8.GetByteCount("synthetic fixture desktop/native/Core.cs")));
        Reject(()=>PackageArchive.Verify(changed),"hash");
        string extra=NewPath(); File.Copy(original,extra); Rewrite(extra,".cache/leaked.txt","unlisted");
        Reject(()=>PackageArchive.Verify(extra),"path");
        string unlisted=NewPath(); File.Copy(original,unlisted); Rewrite(unlisted,"desktop/native/Unlisted.cs","unlisted");
        Reject(()=>PackageArchive.Verify(unlisted),"manifest");
        string missing=NewPath(); File.Copy(original,missing);
        using(var archive=ZipFile.Open(missing,ZipArchiveMode.Update)) archive.GetEntry("desktop/native/Core.cs").Delete();
        Reject(()=>PackageArchive.Verify(missing),"manifest");
        string duplicate=NewPath(); File.Copy(original,duplicate);
        using(var archive=ZipFile.Open(duplicate,ZipArchiveMode.Update))
        using(var writer=new StreamWriter(archive.CreateEntry("desktop/native/Core.cs").Open())) writer.Write("duplicate");
        Reject(()=>PackageArchive.Verify(duplicate),"duplicate");
        string partial=NewPath(); File.Copy(original,partial);
        using(var archive=ZipFile.OpenRead(partial))
        using(var reader=new StreamReader(archive.GetEntry("manifest.json").Open()))
        {
            var manifest=JsonNode.Parse(reader.ReadToEnd());
            manifest["schemaVersion"]=999;
            string text=manifest.ToJsonString();
            reader.Dispose(); archive.Dispose(); Rewrite(partial,"manifest.json",text);
        }
        Reject(()=>PackageArchive.Verify(partial),"manifest");
        Console.WriteLine("PASS: actual ZIP verification rejects tampering, private/unlisted/missing files, duplicate entries and unsupported manifests.");
    }
}
