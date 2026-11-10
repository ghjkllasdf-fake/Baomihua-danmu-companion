using System;
using System.ComponentModel;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Win32.SafeHandles;

namespace Popcorn;

internal sealed class NativePlayerRateSource : IDisposable
{
    // DAC/metadata cold initialization is independent of the sampling cadence.
    // Keep it bounded without applying the shorter interactive-call budget.
    private const int DefaultDiscoveryTimeoutMs=8000;
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    private readonly object gate=new();
    private PlayerRateBinding binding;
    private int pid,generation;
    private double retryAt;
    private bool discovering,disposed,restartRequired;
    private readonly Func<int,(PlayerRateBinding Binding,bool Unsupported)> locate;
    private readonly int discoveryTimeoutMs;

    internal NativePlayerRateSource():this(target=>{var found=Locate(target,out bool unsupported);return(found,unsupported);}) { }
    internal NativePlayerRateSource(Func<int,(PlayerRateBinding Binding,bool Unsupported)> locate,int discoveryTimeoutMs=DefaultDiscoveryTimeoutMs)
    {
        this.locate=locate??throw new ArgumentNullException(nameof(locate));
        if(discoveryTimeoutMs<1)throw new ArgumentOutOfRangeException(nameof(discoveryTimeoutMs));
        this.discoveryTimeoutMs=discoveryTimeoutMs;
    }

    internal double? Read(VideoWindow window)
    {
        uint selected=0;if(window!=null)GetWindowThreadProcessId(window.Handle,out selected);
        return ReadProcess(selected<=int.MaxValue ? (int)selected : 0);
    }
    internal double? ReadProcess(int selected)
    {
        lock(gate)
        {
            if(disposed)return null;
            if(restartRequired)throw new ProbeRestartException();
            if(pid!=selected){binding?.Dispose();binding=null;pid=selected;generation++;retryAt=0;}
            if(selected<1)return null;
            if(binding!=null)
            {
                try{if(binding.TryRead(out double rate))return rate;}
                catch(Exception error) when(error is not OutOfMemoryException){}
                binding.Dispose();binding=null;
            }
            if(!discovering && Monotonic.Now>=retryAt)
            {
                discovering=true;int target=pid,epoch=generation;
                _=Task.Run(()=>{
                    PlayerRateBinding found=null;bool unsupported=false,restart=false;
                    var operation=Task.Run(()=>locate(target));
                    try
                    {
                        ProbeOperation.Complete(()=>operation,discoveryTimeoutMs,CancellationToken.None);
                        var result=operation.GetAwaiter().GetResult();found=result.Binding;unsupported=result.Unsupported;
                    }
                    catch(ProbeRestartException)
                    {
                        restart=true;
                        _=operation.ContinueWith(done=>{if(done.Status==TaskStatus.RanToCompletion)done.Result.Binding?.Dispose();else _=done.Exception;},TaskScheduler.Default);
                    }
                    catch(OutOfMemoryException){restart=true;}
                    catch(Exception error) when(error is not OutOfMemoryException){}
                    lock(gate)
                    {
                        discovering=false;
                        if(restart && !disposed)restartRequired=true;
                        if(disposed || epoch!=generation){found?.Dispose();return;}
                        binding=found;retryAt=Monotonic.Now+(unsupported ? 60000 : 3000);
                    }
                });
            }
        }
        return null;
    }
    private static bool HashMatches(string path,string expected)
    {
        if(!File.Exists(path))return false;
        using var file=File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).Equals(expected,StringComparison.OrdinalIgnoreCase);
    }
    private const string DacHash="6F7A3520FB5A30D1C747E7D232B219C1C97A2270429DA7AA1F572AC2C60B28BE";
    private static ulong Offset(ClrObject value,string field)
    {
        var metadata=value.Type?.GetFieldByName(field) ?? throw new InvalidDataException("Player field unavailable.");
        return checked(metadata.GetAddress(value.Address)-value.Address);
    }
    private static PlayerRateBinding Locate(int pid,out bool unsupported)
    {
        unsupported=false;
        using var process=Process.GetProcessById(pid);
        long processCreated=process.StartTime.ToUniversalTime().ToFileTimeUtc();
        string directory=Path.GetDirectoryName(process.MainModule.FileName);
        // These offsets/getter belong to the inspected 1.2.2 artifact. Never
        // execute an unverified target DAC or guess layouts of another build.
        if(!process.ProcessName.Equals("BaoMiHua",StringComparison.OrdinalIgnoreCase) ||
            !HashMatches(Path.Combine(directory,"BaoMiHua.dll"),"4075FAF0C522C88D0D22E975583C3C00DE9EBDCABF4428141C085C2903BDA2BA") ||
            !HashMatches(Path.Combine(directory,"playerkit.dll"),"6AA0111F8DEC16D88CC61659D3ED2996C0C3702C0557EFC05EA008739767D66A"))
        {unsupported=true;return null;}
        // Use the application's pinned diagnostic image, never load or lock a
        // DLL from the player's installation. The app installer owns this file.
        string dacPath=Path.Combine(AppContext.BaseDirectory,"Baomihua.PlayerRate.Dac.dll");
        using var dac=new FileStream(dacPath,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(!Convert.ToHexString(SHA256.HashData(dac)).Equals(DacHash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid diagnostic cache image.");
        DataTarget target=DataTarget.AttachToProcess(pid,suspend:false);
        try
        {
            var runtime=target.ClrVersions.Single().CreateRuntime(dacPath);
            var module=runtime.EnumerateModules().Single(item=>string.Equals(Path.GetFileName(item.Name),"BaoMiHua.dll",StringComparison.OrdinalIgnoreCase));
            if(module.MetadataLength is <1 or >16*1024*1024)return null;
            var metadata=new byte[(int)module.MetadataLength];
            if(target.DataReader.Read(module.MetadataAddress,metadata)!=metadata.Length)return null;
            using var providerMetadata=MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange(metadata));
            var reader=providerMetadata.GetMetadataReader();
            if(reader.GetGuid(reader.GetModuleDefinition().Mvid)!=new Guid("6ac6abaa-0316-4746-a5c1-5094e919785f")){unsupported=true;return null;}
            var nativeModule=target.EnumerateModules().Single(item=>string.Equals(Path.GetFileName(item.FileName),"playerkit.dll",StringComparison.OrdinalIgnoreCase));
            if(!Path.GetFullPath(nativeModule.FileName).Equals(Path.GetFullPath(Path.Combine(directory,"playerkit.dll")),StringComparison.OrdinalIgnoreCase))return null;
            ulong nativeMinimum=nativeModule.ImageBase,nativeMaximum=checked(nativeMinimum+(ulong)nativeModule.ImageSize);
            var appType=module.GetTypeByName("Filmly.App");
            if(appType==null)return null;
            var appHandle=runtime.EnumerateHandles().FirstOrDefault(handle=>handle.HandleKind==ClrHandleKind.RefCounted && handle.Object.Type?.MethodTable==appType.MethodTable);
            if(appHandle==null)return null;
            var app=appHandle.Object;var host=app.ReadObjectField("cR");
            var provider=host.ReadObjectField("<Services>k__BackingField");var scope=provider.ReadObjectField("<Root>k__BackingField");
            var list=scope.ReadObjectField("_disposables");var itemsObject=list.ReadObjectField("_items");var items=itemsObject.AsArray();
            int count=list.ReadField<int>("_size");
            if(count is <1 or >4096 || count>items.Length)return null;
            var references=items.ReadValues<ulong>(0,count);
            if(references==null)return null;
            int chosen=-1;ClrObject selectedService=default,selectedPlayer=default,selectedNative=default;
            for(int i=0;i<references.Length;i++)
            {
                var service=runtime.Heap.GetObject(references[i]);
                if(service.Type?.Name!="Filmly.Services.MediaPlayerKitService")continue;
                var player=service.ReadObjectField("SU");
                if(player.IsNull || player.Type?.Name!="Filmly.Playback.MediaPlayerKitPlayer" || player.ReadField<bool>("BoW"))continue;
                var native=player.ReadObjectField("Bol");if(native.IsNull || native.Type?.Name!="a.aI")continue;
                if(chosen>=0)return null;
                chosen=i;selectedService=service;selectedPlayer=player;selectedNative=native;
            }
            if(chosen<0)return null;
            ulong[] offsets={Offset(app,"cR"),Offset(host,"<Services>k__BackingField"),Offset(provider,"<Root>k__BackingField"),
                Offset(scope,"_disposables"),Offset(list,"_items"),items.Type.GetArrayElementAddress(items.Address,chosen)-items.Address,
                Offset(selectedService,"SU"),Offset(selectedPlayer,"Bol"),Offset(selectedNative,"TN")};
            ulong[] types={app.Type.MethodTable,host.Type.MethodTable,provider.Type.MethodTable,scope.Type.MethodTable,list.Type.MethodTable,
                itemsObject.Type.MethodTable,selectedService.Type.MethodTable,selectedPlayer.Type.MethodTable,selectedNative.Type.MethodTable};
            var result=new PlayerRateBinding(new ProcessMemory(pid,processCreated),appHandle.Address,offsets,types,7,Offset(selectedPlayer,"BoW"),nativeMinimum,nativeMaximum);
            if(!result.TryRead(out _)){result.Dispose();return null;}
            return result;
        }
        finally{target?.Dispose();}
    }
    private sealed class ProcessMemory : IPlayerRateMemory
    {
        [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool ReadProcessMemory(SafeProcessHandle process,IntPtr address,ref byte buffer,nuint count,out nuint read);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exited,out long kernel,out long user);
        private readonly SafeProcessHandle process;
        internal ProcessMemory(int pid,long expectedCreation)
        {
            process=OpenProcess(0x1010,false,pid); // Read memory and query identity only.
            if(process.IsInvalid){process.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
            if(!GetProcessTimes(process,out long created,out _,out _,out _) || created!=expectedCreation)
            {process.Dispose();throw new InvalidDataException("Player process identity changed.");}
        }
        public bool Read(ulong address,Span<byte> destination)=>destination.IsEmpty ||
            ReadProcessMemory(process,(IntPtr)(long)address,ref MemoryMarshal.GetReference(destination),(nuint)destination.Length,out nuint read) && read==(nuint)destination.Length;
        public void Dispose()=>process.Dispose();
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;generation++;binding?.Dispose();binding=null;}}
}
