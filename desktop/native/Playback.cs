using System;
using System.IO;

namespace Popcorn;

public sealed class PlayerProbe : IDisposable
{
    private readonly ProbeSupervisor supervisor;
    public PlayerSample Latest=>supervisor.Latest;
    internal bool ReceivedFrame=>supervisor.ReceivedFrame;
    public event Action SampleUpdated {add=>supervisor.Updated+=value;remove=>supervisor.Updated-=value;}
    public PlayerProbe(string projectRoot):this(projectRoot,false) { }
    internal PlayerProbe(string projectRoot,bool toolbar)
    {
        string root=Path.GetFullPath(projectRoot);
        supervisor=new ProbeSupervisor(()=>new ProbeProcess(Path.Combine(root,"DanmuCompanion.Probe.exe"),toolbar ? new[]{"--toolbar"} : Array.Empty<string>()));
    }
    public static double? ParseTime(string text)=>PlayerSampler.ParseTime(text);
    public void Dispose()=>supervisor.Dispose();
}
