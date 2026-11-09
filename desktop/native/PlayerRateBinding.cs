using System;

namespace Popcorn;

internal interface IPlayerRateMemory : IDisposable
{
    bool Read(ulong address,Span<byte> destination);
}

// The locator supplies a GC-handle slot and field offsets, not cached object
// addresses. Each read follows the current references, including after a GC.
internal sealed class PlayerRateBinding : IDisposable
{
    private static readonly byte[] Getter=Convert.FromHexString("8B8120030000660F6EC0C3");
    private readonly IPlayerRateMemory memory;
    private readonly ulong rootSlot,disposedOffset;
    private readonly ulong getterMinimum,getterMaximum;
    private readonly ulong[] offsets,types;
    private readonly int playerIndex;
    private readonly object gate=new();
    private bool disposed;

    internal PlayerRateBinding(IPlayerRateMemory memory,ulong rootSlot,ulong[] offsets,ulong[] types,int playerIndex,ulong disposedOffset,ulong getterMinimum=0,ulong getterMaximum=ulong.MaxValue)
    {
        if(memory==null || offsets==null || types==null || offsets.Length!=types.Length || offsets.Length is <1 or >16 ||
            playerIndex<0 || playerIndex>=offsets.Length || disposedOffset>4096)throw new ArgumentException("Invalid player-rate binding.");
        foreach(ulong offset in offsets)if(offset>65536)throw new ArgumentException("Invalid player-rate field offset.");
        this.memory=memory;this.rootSlot=rootSlot;this.offsets=(ulong[])offsets.Clone();this.types=(ulong[])types.Clone();
        this.playerIndex=playerIndex;this.disposedOffset=disposedOffset;
        this.getterMinimum=getterMinimum;this.getterMaximum=getterMaximum;
    }
    private static bool Pointer(ulong address,int bytes=8)=>address>=0x10000 && address<=0x00007fffffffffffUL-(ulong)bytes;
    private bool ReadPointer(ulong address,out ulong value)
    {
        Span<byte> bytes=stackalloc byte[8];value=0;
        if(!Pointer(address) || !memory.Read(address,bytes))return false;
        value=BitConverter.ToUInt64(bytes);return Pointer(value);
    }
    private bool Graph(out ulong handle,out ulong engine)
    {
        handle=engine=0;
        if(!ReadPointer(rootSlot,out ulong current))return false;
        Span<byte> flag=stackalloc byte[1];
        for(int i=0;i<offsets.Length;i++)
        {
            if(!ReadPointer(current,out ulong table) || (table&~3UL)!=types[i])return false;
            if(i==playerIndex)
            {
                if(!memory.Read(current+disposedOffset,flag) || flag[0]!=0)return false;
            }
            if(!ReadPointer(current+offsets[i],out current))return false;
        }
        handle=current;
        return ReadPointer(handle,out ulong inner) && ReadPointer(inner+0x90,out engine);
    }
    internal bool TryRead(out double rate)
    {
        lock(gate)
        {
            rate=0;if(disposed || !Graph(out ulong handle,out ulong engine) ||
                !ReadPointer(engine,out ulong table) || !ReadPointer(table+0x50,out ulong getter))return false;
            if(getter<getterMinimum || getter>=getterMaximum || getterMaximum-getter<11)return false;
            Span<byte> code=stackalloc byte[11];Span<byte> value=stackalloc byte[4];
            if(!memory.Read(getter,code) || !code.SequenceEqual(Getter) || !memory.Read(engine+0x320,value))return false;
            double observed=BitConverter.ToSingle(value);
            if(!double.IsFinite(observed) || observed<=0 || observed>16 ||
                !Graph(out ulong nextHandle,out ulong nextEngine) || handle!=nextHandle || engine!=nextEngine)return false;
            rate=observed;return true;
        }
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;memory.Dispose();}}
}
