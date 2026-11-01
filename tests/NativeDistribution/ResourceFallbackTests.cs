using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Runtime.Loader;

namespace Popcorn;

internal static class ResourceFallbackTests
{
    private sealed class Resources(string root) : AssemblyLoadContext(true)
    {
        protected override Assembly Load(AssemblyName name)
        {
            string directory=string.IsNullOrEmpty(name.CultureName) ? root : Path.Combine(root,name.CultureName);
            string file=Path.Combine(directory,name.Name+".dll");
            return File.Exists(file) ? LoadFromAssemblyPath(file) : null;
        }
    }
    internal static void Run(string root)
    {
        root=Path.GetFullPath(root);
        foreach(string directory in Directory.EnumerateDirectories(root))
            if(Directory.EnumerateFiles(directory,"*.resources.dll").Any())
                DistributionTests.Check(Path.GetFileName(directory) is "en" or "zh-Hans" or "zh-Hant","A customer payload must not retain unrelated native language resources.");
        var context=new Resources(root);
        try
        {
            var assembly=context.LoadFromAssemblyPath(Path.Combine(root,"System.Windows.Forms.dll"));
            string resource=assembly.GetManifestResourceNames().Single(name=>name.EndsWith("SR.resources",StringComparison.Ordinal));
            using var stream=assembly.GetManifestResourceStream(resource);using var reader=new ResourceReader(stream);
            var entries=reader.GetEnumerator();string key=null;
            while(entries.MoveNext())if(entries.Key is string candidate && candidate=="InvalidArgument"){key=candidate;break;}
            DistributionTests.Check(key!=null,"The resource fallback fixture must use an actual neutral WinForms diagnostic resource.");
            var manager=new ResourceManager(resource[..^".resources".Length],assembly);
            string english=manager.GetString(key,CultureInfo.GetCultureInfo("en-US"));
            DistributionTests.Check(!string.IsNullOrWhiteSpace(english),"English must remain available from neutral resources without an en-US directory.");
            foreach(string culture in new[]{"zh-CN","zh-TW"})
            {
                string value=manager.GetString(key,CultureInfo.GetCultureInfo(culture));
                DistributionTests.Check(!string.IsNullOrWhiteSpace(value) && value!=english,"Chinese regional cultures must resolve their retained Hans/Hant resources.");
            }
            DistributionTests.Check(manager.GetString(key,CultureInfo.GetCultureInfo("de-DE"))==english,"A trimmed culture must fall back to the retained neutral English resource.");
            manager.ReleaseAllResources();
        }
        finally{context.Unload();}
        Console.WriteLine("PASS: exact payload WinForms resources, Chinese Hans/Hant resolution and neutral English fallback for a trimmed culture; OS language unchanged.");
    }
}
