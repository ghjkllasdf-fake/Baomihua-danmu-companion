using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Popcorn;

internal sealed class ApplicationInstance : IDisposable
{
    private readonly Mutex mutex;
    private bool released;
    public bool Acquired {get;}
    public ApplicationInstance(ApplicationPaths paths)
    {
        string Normalize(string path)=>Path.GetFullPath(path).TrimEnd('\\','/').ToUpperInvariant();
        string profile=Normalize(paths.ProfileRoot);
        string ordinary=Normalize(new ApplicationPaths(paths.Root).ProfileRoot);
        string name=@"Local\BaomihuaDanmuCompanion";
        if(profile!=ordinary)name+="-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile)))[..20];
        mutex=new Mutex(false,name);
        try{Acquired=mutex.WaitOne(0);}
        catch(AbandonedMutexException){Acquired=true;}
    }
    public void Dispose()
    {
        if(released)return;released=true;
        if(Acquired)mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
