using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

public static class ProbeGate
{
    public static async Task OpenAsync(Stream input,TimeSpan timeout,CancellationToken stop)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop); deadline.CancelAfter(timeout);
        byte[] expected="start\n"u8.ToArray(),bytes=new byte[6]; int count=0;
        while (count<bytes.Length)
        {
            int read=await input.ReadAsync(bytes.AsMemory(count),deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
            if (read==0) throw new EndOfStreamException("The probe start gate was closed.");
            count+=read;
        }
        if (!bytes.AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Invalid probe start gate.");
    }
}
