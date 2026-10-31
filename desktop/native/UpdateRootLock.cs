using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Popcorn;

internal sealed class UpdateRootLock : IDisposable
{
    private readonly ManualResetEventSlim ready=new(false),stop=new(false);
    private readonly Thread owner;
    private Exception failure;
    private bool disposed;
    internal bool Acquired {get;private set;}
    internal UpdateRootLock(string root)
    {
        string name=@"Local\DanmuCompanion-root-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(UpdatePackage.SafeRoot(root).ToUpperInvariant())));
        // Mutex ownership is thread-affine; async updater continuations must not release it.
        owner=new Thread(()=>
        {
            try
            {
                using var mutex=new Mutex(false,name);
                try{Acquired=mutex.WaitOne(0);}catch(AbandonedMutexException){Acquired=true;}
                ready.Set();if(!Acquired)return;
                try{stop.Wait();}finally{mutex.ReleaseMutex();}
            }
            catch(Exception error){failure=error;ready.Set();}
        }){IsBackground=true,Name="Application update ownership"};
        owner.Start();ready.Wait();if(failure!=null){Dispose();throw new InvalidOperationException("无法取得应用更新锁。",failure);}
    }
    public void Dispose(){if(disposed)return;disposed=true;stop.Set();owner.Join();ready.Dispose();stop.Dispose();}
}
