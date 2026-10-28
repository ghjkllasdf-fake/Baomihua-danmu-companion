using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

public static class WorkerFixture
{
    public static void Emit()
    {
        var frame=new {version=1,sequence=1,window=new {handle=1L,title="worker-fixture",x=-10,y=0,width=1280,height=720,dpiScale=(double?)1,
            windowVisible=(bool?)true,windowMinimized=(bool?)false,isForeground=(bool?)null},title="worker-fixture",episodeNumber=1,positionMs=1000,
            playing=false,stampMs=Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency,detail=Thread.CurrentThread.GetApartmentState().ToString(),source="uia-text",positionResolutionMs=200,durationMs=60000};
        var output=Console.OpenStandardOutput(); output.Write(JsonSerializer.SerializeToUtf8Bytes(frame)); output.WriteByte(10); output.Flush();
        Thread.Sleep(Timeout.Infinite);
    }
}
public sealed class WorkerGateStream : Stream
{
    private readonly Stream input=Console.OpenStandardInput();
    private int announced;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken stop=default)
    {
        if (Interlocked.Exchange(ref announced,1)==0) { var output=Console.OpenStandardOutput(); output.Write("gate-ready\n"u8); output.Flush(); }
        return input.ReadAsync(buffer,stop);
    }
    public override bool CanRead=>true;
    public override bool CanSeek=>false;
    public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException();
    public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long value)=>throw new NotSupportedException();
    public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
