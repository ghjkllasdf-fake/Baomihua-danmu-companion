using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

public sealed class ProbeFrameReader(Stream input)
{
    private readonly Stream input=input??throw new ArgumentNullException(nameof(input));
    private readonly byte[] chunk=new byte[4096],frame=new byte[ProbeProtocol.MaxFrameBytes];
    private int position,available,busy,terminal;
    private Task<int> pending;
    public bool IsQuiescent=>Volatile.Read(ref pending)?.IsCompleted??true;
    public async Task<byte[]> ReadAsync(TimeSpan timeout,CancellationToken stop)
    {
        if (timeout<=TimeSpan.Zero || timeout>TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (Interlocked.CompareExchange(ref busy,1,0)!=0) throw new InvalidOperationException("A probe read is already in progress.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);
        try
        {
            if (Volatile.Read(ref terminal)!=0) throw new InvalidOperationException("The probe reader has terminated.");
            deadline.CancelAfter(timeout); int length=0;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (position==available)
                {
                    // A deadline ends the wait, not necessarily the pipe operation. Never reuse this reader after failure.
                    var read=input.ReadAsync(chunk.AsMemory(),deadline.Token).AsTask(); Volatile.Write(ref pending,read);
                    _=read.ContinueWith(static task=>{ _=task.Exception; },CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
                    available=await read.WaitAsync(deadline.Token).ConfigureAwait(false); position=0;
                    if (available==0) throw new EndOfStreamException("The probe stream ended before a complete frame.");
                    if (available<0 || available>chunk.Length) throw new IOException("Invalid probe stream read.");
                }
                byte next=chunk[position++];
                if (next==(byte)'\n')
                {
                    if (length==0) throw new InvalidDataException("Empty probe frame.");
                    return frame.AsSpan(0,length).ToArray();
                }
                if (length==frame.Length) throw new InvalidDataException("Probe frame exceeds its byte budget.");
                frame[length++]=next;
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            Volatile.Write(ref terminal,1); throw new TimeoutException("The complete probe frame deadline expired.");
        }
        catch { Volatile.Write(ref terminal,1); throw; }
        finally { Volatile.Write(ref busy,0); }
    }
}
