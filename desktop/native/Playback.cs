using System;
using System.IO;

namespace Popcorn;

public sealed class PlayerProbe : IDisposable
{
    private readonly ProbeSupervisor supervisor;
    public PlayerSample Latest=>supervisor.Latest;
    public event Action SampleUpdated {add=>supervisor.Updated+=value;remove=>supervisor.Updated-=value;}
    public PlayerProbe(string projectRoot)
    {
        string root=Path.GetFullPath(projectRoot);
        supervisor=new ProbeSupervisor(()=>new ProbeProcess(Path.Combine(root,".deps","pwsh","pwsh.exe"),Path.Combine(root,"desktop","ProbeWorker.ps1")));
    }
    public static double? ParseTime(string text)=>PlayerSampler.ParseTime(text);
    public void Dispose()=>supervisor.Dispose();
}
