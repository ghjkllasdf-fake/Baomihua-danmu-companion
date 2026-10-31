using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

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
        Console.WriteLine("PASS: bounded worker framing, current-user pipes, PID checks and forged job rejection.");
    }
}
