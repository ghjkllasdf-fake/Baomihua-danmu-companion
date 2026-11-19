using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal static class ToolbarProbeRuntime
{
    internal static void Run()
    {
        using var output=Console.OpenStandardOutput();
        using var reader=new ToolbarReader();
        long sequence=0;
        double scanAt=0;
        VideoWindow window=null;
        while(true)
        {
            double now=Monotonic.Now;
            if(now>=scanAt)
            {
                window=NativeWindows.FindPlayer(window?.Title);
                if(window?.Title is null or "" or "网易爆米花" or "爆米花" || PlayerPrivilege.IsBlocked(window))window=null;
                scanAt=now+200;
            }
            else if(window!=null)
            {
                var previous=window;
                window=NativeWindows.Read(previous.Handle);
                if(window?.ProcessId!=previous.ProcessId)window=null;
            }
            bool? visible=reader.Read(window);
            var sample=new PlayerSample(window,"",0,null,null,Monotonic.Now,"","unavailable",ControlsVisible:visible);
            output.Write(ProbeProtocol.Encode(sample,checked(++sequence)));output.WriteByte(10);output.Flush();
            Thread.Sleep(window==null ? 180 : 30);
        }
    }

    private sealed class ToolbarReader:IDisposable
    {
        private readonly string dac=Path.Combine(AppContext.BaseDirectory,"Baomihua.PlayerRate.Dac.dll");
        private ToolbarBinding binding;
        private Task<ToolbarBinding> opening;
        private CancellationTokenSource deadline;
        private (IntPtr Handle,uint Process) current,requested;
        private double retryAt;
        private int misses;

        internal bool? Read(VideoWindow window)
        {
            var target=window==null ? default : (window.Handle,window.ProcessId);
            if(current!=target)
            {
                current=target;binding?.Dispose();binding=null;retryAt=0;misses=0;deadline?.Cancel();
            }
            if(opening?.IsCompleted==true)
            {
                try
                {
                    var result=opening.GetAwaiter().GetResult();
                    if(requested==current && !deadline.IsCancellationRequested)binding=result;
                    else result?.Dispose();
                }
                catch(Exception error) when(error is not OutOfMemoryException){ }
                finally{opening=null;deadline.Dispose();deadline=null;}
                retryAt=Monotonic.Now+(requested==current ? 5000 : 0);
            }
            if(current.Process==0 || current.Process>int.MaxValue || window.WindowMinimized==true || window.WindowVisible==false)return null;
            if(binding!=null)
            {
                bool? hidden;
                try{hidden=binding.Read();}
                catch(Exception error) when(error is not OutOfMemoryException){hidden=null;}
                if(hidden.HasValue){misses=0;return !hidden.Value;}
                if(++misses<5)return null;
                binding.Dispose();binding=null;misses=0;retryAt=Monotonic.Now+500;
            }
            if(opening==null && Monotonic.Now>=retryAt)
            {
                deadline=new(TimeSpan.FromSeconds(8));
                var token=deadline.Token;requested=current;int pid=(int)current.Process;
                opening=Task.Run(()=>ToolbarBinding.Open(pid,dac,token));
            }
            return null;
        }

        public void Dispose(){deadline?.Cancel();binding?.Dispose();}
    }
}

internal static class ToolbarLayoutProof
{
    // Fingerprint the actual compiled control layout, not the player version or
    // whole binary. A changed layout never inherits coordinates from this one.
    private const int LayoutBytes=6837;
    private static readonly byte[] LayoutHash=Convert.FromHexString("9821FB1DF7BE38043EF11025565ABDB0D6F8A6782AEBC5B32D59E78D5AFAE5B1");

    internal static bool Matches(string path)
    {
        using var stream=File.OpenRead(path);
        if(stream.Length is <LayoutBytes or >16*1024*1024)return false;
        var buffer=new byte[(int)stream.Length];stream.ReadExactly(buffer);
        return Matches(buffer);
    }

    internal static bool Matches(ReadOnlySpan<byte> buffer)
    {
        int matches=0,candidates=0;
        for(int i=0;i<=buffer.Length-LayoutBytes;i++)
        {
            var start=buffer[i..];
            if(!start.StartsWith("XBF\0"u8))continue;
            int meta=BinaryPrimitives.ReadInt32LittleEndian(start[4..]);
            int nodes=BinaryPrimitives.ReadInt32LittleEndian(start[8..]);
            if(meta<=0 || nodes<=0 || 12L+meta+nodes!=LayoutBytes)continue;
            if(++candidates>64)return false;
            if(SHA256.HashData(start[..LayoutBytes]).AsSpan().SequenceEqual(LayoutHash) && ++matches>1)return false;
        }
        return matches==1;
    }
}
