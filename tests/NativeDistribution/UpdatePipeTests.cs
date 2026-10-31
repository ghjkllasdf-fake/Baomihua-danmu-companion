using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Popcorn;

internal static class UpdatePipeTests
{
    internal static async Task Run()
    {
        using var memory=new MemoryStream();await UpdatePipe.WriteAsync(memory,new{ready=true},CancellationToken.None);memory.Position=0;
        using(var message=await UpdatePipe.ReadAsync(memory,CancellationToken.None))DistributionTests.Check(message.RootElement.GetProperty("ready").GetBoolean(),"Bounded update framing must round-trip.");
        byte[] header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,16385);using var oversized=new MemoryStream(header);
        UpdatePackageTests.Reject(()=>UpdatePipe.ReadAsync(oversized,CancellationToken.None).GetAwaiter().GetResult(),"Oversized worker messages must be rejected before allocation.");
        string name="DanmuUpdateTest-"+Guid.NewGuid().ToString("N");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        var connecting=server.WaitForConnectionAsync(timeout.Token);await client.ConnectAsync(timeout.Token);await connecting;
        UpdatePipe.CheckPeer(server,Environment.ProcessId,true);UpdatePipe.CheckPeer(client,Environment.ProcessId,false);
        UpdatePackageTests.Reject(()=>UpdatePipe.CheckPeer(server,int.MaxValue,true),"A different worker PID must not be accepted.");
        UpdatePackageTests.Reject(()=>UpdateWorker.Validate(new UpdateJob("relative",Path.GetTempPath(),"app",new UpdateDownload("relative","",0,"dev","standard","portable","relative"),false,Guid.NewGuid().ToString("N")),Path.GetTempPath()),"Forged worker jobs must not derive a writable application root.");
        var info=new ProcessStartInfo(Environment.ProcessPath){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true};
        info.ArgumentList.Add("--image-fixture");using var parent=Process.Start(info);
        try
        {
            DistributionTests.Check(await parent.StandardOutput.ReadLineAsync()=="ready","Controlled handoff parent must be running.");
            using var expiredPreflight=new CancellationTokenSource(TimeSpan.FromMilliseconds(10));var waiting=UpdateWorker.WaitForCommittedParent(parent);
            await Task.Delay(100);DistributionTests.Check(expiredPreflight.IsCancellationRequested && !waiting.IsCompleted,"Expired preflight budget must not terminate an already ACKed handoff.");
            await parent.StandardInput.WriteLineAsync("exit");await parent.StandardInput.FlushAsync();await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            DistributionTests.Check(parent.HasExited,"Committed writer must retain the exact parent handle until actual exit.");
        }
        finally{if(!parent.HasExited){parent.Kill(true);await parent.WaitForExitAsync();}}
        Console.WriteLine("PASS: bounded worker framing, current-user pipes, PID checks and forged job rejection.");
    }
}
