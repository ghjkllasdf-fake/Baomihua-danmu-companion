using System;
using System.IO;
using System.Text;

namespace Popcorn;

internal static class NativePreferenceTests
{
    internal static void Run()
    {
        string folder=Path.Combine(Path.GetTempPath(),"DanmuNativeApi-"+Guid.NewGuid().ToString("N"));
        string path=Path.Combine(folder,"api.dat"),address="https://danmu.example.com/EXAMPLE_TOKEN_ONLY";
        try
        {
            var missing=ApiPreferences.Load(path);
            DistributionTests.Check(!missing.Saved && missing.ApiBase=="" && missing.Notice=="","Missing API configuration must start empty.");
            ApiPreferences.Save(path,address+"/api/v2/");byte[] bytes=File.ReadAllBytes(path);
            DistributionTests.Check(!Encoding.UTF8.GetString(bytes).Contains("EXAMPLE_TOKEN_ONLY"),"API file must not contain plaintext token.");
            var loaded=ApiPreferences.Load(path);
            DistributionTests.Check(loaded.Saved && loaded.ApiBase==address,"Native runtime must restore normalized DPAPI CurrentUser configuration.");
            string before=Convert.ToHexString(bytes);
            try{ApiPreferences.Save(path,"file:///not-an-api");throw new Exception("Invalid API was accepted.");}catch(ArgumentException){}
            DistributionTests.Check(Convert.ToHexString(File.ReadAllBytes(path))==before,"Invalid replacement must preserve saved API.");
            bytes[20]^=127;File.WriteAllBytes(path,bytes);var broken=ApiPreferences.Load(path);
            DistributionTests.Check(!broken.Saved && broken.ApiBase=="" && broken.Notice!="" && !broken.Notice.Contains("EXAMPLE_TOKEN_ONLY"),"Corrupt API must fail closed without leaking secrets.");
            DistributionTests.Check(File.Exists(path),"Failed reads must preserve original encrypted data.");
            File.WriteAllBytes(path,new byte[16385]);DistributionTests.Check(!ApiPreferences.Load(path).Saved,"Oversized encrypted data must be rejected.");
            ApiPreferences.Save(path,address);
            using(var locked=File.Open(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                try{ApiPreferences.Save(path,"https://danmu.example.com/NEW_EXAMPLE");throw new Exception("Blocked API write succeeded.");}
                catch(Exception error) when(error is IOException or UnauthorizedAccessException){}
            }
            DistributionTests.Check(ApiPreferences.Load(path).ApiBase==address,"Failed write must preserve previous API.");
            ApiPreferences.Save(path,"");DistributionTests.Check(!File.Exists(path) && !ApiPreferences.Load(path).Saved,"Explicit clearing must remove saved secret.");
        }
        finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
        Console.WriteLine("PASS: native CurrentUser encryption, no plaintext, normalization, tamper/size rejection, atomic failure and removal.");
    }
}
