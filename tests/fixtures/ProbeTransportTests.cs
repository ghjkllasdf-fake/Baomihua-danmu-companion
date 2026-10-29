using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

public static class ProbeTransportTests
{
    private static void Check(bool value,string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid probe data was accepted."); }
    private static PlayerSample Sample()=>new(new VideoWindow(new IntPtr(0x123456789abL),"movie 第2集",-1920,-120,1920,1080)
        {DpiScale=1.5,WindowVisible=true,WindowMinimized=false,IsForeground=null,ProcessId=1234},"movie",2,10000.25,false,1000,"window client","uia-seekbar-ms+smtc",200,60000);
    private static async Task RejectAsync<T>(Func<Task> action) where T:Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected failure was not reported: "+typeof(T).Name);
    }
    public static async Task Run(string group)
    {
        if (group is "Protocol" or "All")
        {
            var sample=Sample(); var bytes=ProbeProtocol.Encode(sample,1);
            var roundtrip=ProbeProtocol.Decode(bytes,0,1200);
            Check(roundtrip.Sequence==1 && roundtrip.Sample==sample,"A nonzero full-width HWND, nullable metadata and negative coordinates did not roundtrip.");
            Check(roundtrip.Sample.Window.ProcessId==1234,"The sampled video process identity was not preserved across the worker boundary.");
            var missing=new PlayerSample(null,"",0,null,null,1000,"missing","unavailable");
            Check(ProbeProtocol.Decode(ProbeProtocol.Encode(missing,2),1,1200).Sample==missing,"An unavailable player gained invented data.");
            var aged=ProbeProtocol.Decode(bytes,0,2600).Sample;
            Check(aged.PositionMs==null && aged.Playing==null && aged.StampMs==1000 && aged.Window==sample.Window && aged.Title==sample.Title,"Transport delay must not renew old playback proof or discard a new media identity.");
            Reject(()=>ProbeProtocol.Decode(bytes,1,1200));
            Reject(()=>ProbeProtocol.Decode(bytes,2,1200));
            Reject(()=>ProbeProtocol.Decode(bytes,0,999));
            Reject(()=>ProbeProtocol.Decode(bytes,0,double.NaN));
            string original=Encoding.UTF8.GetString(bytes);
            foreach (var change in new Action<JsonObject>[] {
                o=>o.Remove("stampMs"), o=>o["stampMs"]=null, o=>o["stampMs"]="1000",o=>o["stampMs"]=-1,
                o=>o["version"]=2,o=>o["sequence"]=0,o=>o["unexpected"]="credential-marker",o=>o["source"]="pretend-automatic",
                o=>o["positionMs"]=-1,o=>o["playing"]="paused",o=>o["durationMs"]=-1,o=>o["positionResolutionMs"]=0,
                o=>o["title"]=new string('x',1025),o=>o["window"]["handle"]=0,o=>o["window"]["width"]=0,
                o=>o["window"]["dpiScale"]=-1,o=>o["window"]["windowVisible"]="true",o=>o["window"]["processId"]=-1,
                o=>o["window"]["processId"]=4294967296L,o=>o["window"]["processId"]="1234",o=>o["window"]["processId"]=1.5,
                o=>o["window"]["processId"]=null,o=>o["window"]=null })
            {
                var invalid=JsonNode.Parse(original).AsObject(); change(invalid);
                Reject(()=>ProbeProtocol.Decode(Encoding.UTF8.GetBytes(invalid.ToJsonString()),0,1200));
            }
            Reject(()=>ProbeProtocol.Decode(Encoding.UTF8.GetBytes(original.Insert(1,"\"sequence\":2,")),0,1200));
            var oldWire=JsonNode.Parse(original).AsObject();oldWire["window"].AsObject().Remove("processId");
            Check(ProbeProtocol.Decode(Encoding.UTF8.GetBytes(oldWire.ToJsonString()),0,1200).Sample.Window.ProcessId==0,
                "A legacy frame without sampled process identity must remain explicitly untrusted, not invent one.");
            Reject(()=>ProbeProtocol.Decode(new byte[ProbeProtocol.MaxFrameBytes+1],0,1200));
            Reject(()=>ProbeProtocol.Decode(new byte[] {255},0,1200));
            Reject(()=>ProbeProtocol.Decode(Encoding.UTF8.GetBytes("null"),0,1200));
            foreach (string field in new[] {"positionMs","durationMs"})
            {
                foreach (double value in new[] {9007199254740992.0,double.MaxValue})
                {
                    var extreme=JsonNode.Parse(original).AsObject(); extreme[field]=value;
                    Reject(()=>ProbeProtocol.Decode(Encoding.UTF8.GetBytes(extreme.ToJsonString()),0,1200));
                }
            }
            var boundary=sample with {PositionMs=9007199254740991,DurationMs=9007199254740991};
            Check(ProbeProtocol.Decode(ProbeProtocol.Encode(boundary,1),0,1200).Sample==boundary,"A value at the engineering measurement boundary was rejected.");
            Reject(()=>ProbeProtocol.Encode(sample with {PositionMs=double.MaxValue},1));
            Reject(()=>ProbeProtocol.Encode(sample with {StampMs=double.MaxValue},1));
            Console.WriteLine("PASS: full probe wire DTO roundtrip, strict schema/sequence/numeric bounds and original sampling freshness.");
        }
        if (group is "Frames" or "All")
        {
            using var chunks=new ShortStream(Encoding.UTF8.GetBytes("first\nsecond\r\n"));
            var reader=new ProbeFrameReader(chunks);
            Check(Encoding.UTF8.GetString(await reader.ReadAsync(TimeSpan.FromSeconds(1),CancellationToken.None))=="first","Short chunks lost the first frame.");
            Check(Encoding.UTF8.GetString(await reader.ReadAsync(TimeSpan.FromSeconds(1),CancellationToken.None))=="second\r","Buffered bytes belonging to the next frame were lost.");
            await RejectAsync<EndOfStreamException>(()=>reader.ReadAsync(TimeSpan.FromSeconds(1),CancellationToken.None));
            using var exact=new MemoryStream(Encoding.UTF8.GetBytes(new string('x',ProbeProtocol.MaxFrameBytes)+"\n"));
            Check((await new ProbeFrameReader(exact).ReadAsync(TimeSpan.FromSeconds(1),CancellationToken.None)).Length==ProbeProtocol.MaxFrameBytes,"The exact byte boundary was rejected.");
            using var huge=new ShortStream(Encoding.UTF8.GetBytes(new string('x',ProbeProtocol.MaxFrameBytes+8192)),4096);
            await RejectAsync<InvalidDataException>(()=>new ProbeFrameReader(huge).ReadAsync(TimeSpan.FromSeconds(1),CancellationToken.None));
            Check(huge.BytesRead<=ProbeProtocol.MaxFrameBytes+4096,"An oversized frame was read in full before rejection.");
            foreach (string partial in new[] {"\n","unfinished"})
            {
                using var input=new MemoryStream(Encoding.UTF8.GetBytes(partial));
                var invalid=new ProbeFrameReader(input);
                if (partial=="\n") await RejectAsync<InvalidDataException>(()=>invalid.ReadAsync(TimeSpan.FromSeconds(1),CancellationToken.None));
                else await RejectAsync<EndOfStreamException>(()=>invalid.ReadAsync(TimeSpan.FromSeconds(1),CancellationToken.None));
                await RejectAsync<InvalidOperationException>(()=>invalid.ReadAsync(TimeSpan.FromSeconds(1),CancellationToken.None));
            }
            using var drip=new DripStream();
            await RejectAsync<TimeoutException>(()=>new ProbeFrameReader(drip).ReadAsync(TimeSpan.FromMilliseconds(80),CancellationToken.None));
            Check(drip.Reads<20,"A byte drip renewed the entire-frame timeout.");
            using var hanging=new HangingStream();
            var stalled=new ProbeFrameReader(hanging);
            await RejectAsync<TimeoutException>(()=>stalled.ReadAsync(TimeSpan.FromMilliseconds(80),CancellationToken.None));
            Check(!stalled.IsQuiescent && hanging.Reads==1,"A timeout was incorrectly treated as completion of the underlying read.");
            await RejectAsync<InvalidOperationException>(()=>stalled.ReadAsync(TimeSpan.FromSeconds(1),CancellationToken.None));
            Check(hanging.Reads==1,"A timed-out reader started a second outstanding read.");
            hanging.Release(); Check(stalled.IsQuiescent,"Underlying read completion was not visible for bounded child cleanup.");
            using var cancelInput=new HangingStream(); using var stop=new CancellationTokenSource();
            var cancelReader=new ProbeFrameReader(cancelInput);
            var cancelled=cancelReader.ReadAsync(TimeSpan.FromSeconds(5),stop.Token); stop.Cancel();
            await RejectAsync<OperationCanceledException>(()=>cancelled); cancelInput.Release();
            using var overlapping=new HangingStream(); var single=new ProbeFrameReader(overlapping);
            var first=single.ReadAsync(TimeSpan.FromSeconds(1),CancellationToken.None);
            await RejectAsync<InvalidOperationException>(()=>single.ReadAsync(TimeSpan.FromSeconds(1),CancellationToken.None));
            overlapping.Release(); await RejectAsync<EndOfStreamException>(()=>first);
            Console.WriteLine("PASS: fixed byte budgets, short/coalesced frames, absolute drip deadline, cancellation and noncooperative-read quarantine.");
        }
    }
}

internal sealed class ShortStream(byte[] bytes,int chunk=7) : MemoryStream(bytes)
{
    public int BytesRead;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken stop=default)
    {
        int read=await base.ReadAsync(buffer[..Math.Min(chunk,buffer.Length)],stop); BytesRead+=read; return read;
    }
}
internal abstract class ReadOnlyFixtureStream : Stream
{
    public override bool CanRead=>true;
    public override bool CanSeek=>false;
    public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException();
    public override long Position { get=>throw new NotSupportedException(); set=>throw new NotSupportedException(); }
    public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long value)=>throw new NotSupportedException();
    public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
internal sealed class HangingStream : ReadOnlyFixtureStream
{
    private readonly TaskCompletionSource<int> completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Reads;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken stop=default) { Reads++; return new(completion.Task); }
    public void Release()=>completion.TrySetResult(0);
}
internal sealed class DripStream : ReadOnlyFixtureStream
{
    public int Reads;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken stop=default)
    {
        await Task.Delay(20,stop); buffer.Span[0]=Reads++==20 ? (byte)'\n' : (byte)'x'; return 1;
    }
}
