using System;
using System.Collections.Generic;

namespace Popcorn;

internal static class PlayerRateBindingTests
{
    internal sealed class Memory : IPlayerRateMemory
    {
        internal readonly Dictionary<ulong,byte> Bytes=new();
        internal Action<ulong> BeforeRead;
        internal int DisposeCount;
        internal void Write(ulong address,byte[] value){for(int i=0;i<value.Length;i++)Bytes[address+(ulong)i]=value[i];}
        internal void Pointer(ulong address,ulong value)=>Write(address,BitConverter.GetBytes(value));
        public bool Read(ulong address,Span<byte> destination)
        {
            BeforeRead?.Invoke(address);
            for(int i=0;i<destination.Length;i++)if(!Bytes.TryGetValue(address+(ulong)i,out destination[i]))return false;
            return true;
        }
        public void Dispose()=>DisposeCount++;
    }
    internal sealed class Fixture
    {
        internal readonly Memory Memory=new();
        internal readonly ulong[] Nodes=new ulong[9],Types=new ulong[9],Offsets=new ulong[9];
        internal PlayerRateBinding Binding;
        internal const ulong Root=0x10000,Handle=0x50000,Engine=0x52000;
        internal Fixture()
        {
            for(int i=0;i<Nodes.Length;i++){Nodes[i]=0x20000+(ulong)i*0x1000;Types[i]=0x100000+(ulong)i*0x100;Offsets[i]=8;}
            Memory.Pointer(Root,Nodes[0]);
            for(int i=0;i<Nodes.Length;i++){Memory.Pointer(Nodes[i],Types[i]);Memory.Pointer(Nodes[i]+8,i==8 ? Handle : Nodes[i+1]);}
            Memory.Write(Nodes[7]+297,new byte[]{0});SetEngine(Handle,1.75f);
            Binding=new(Memory,Root,Offsets,Types,7,297,0x40000,0xa0000);
        }
        internal void SetEngine(ulong handle,float rate)
        {
            ulong inner=handle+0x1000,engine=handle+0x2000,table=handle+0x3000,getter=handle+0x4000;
            Memory.Pointer(handle,inner);Memory.Pointer(inner+0x90,engine);Memory.Pointer(engine,table);Memory.Pointer(table+0x50,getter);
            Memory.Write(getter,Convert.FromHexString("8B8120030000660F6EC0C3"));Memory.Write(engine+0x320,BitConverter.GetBytes(rate));
        }
        internal void ReplacePlayer()
        {
            Memory.Pointer(0x67000,Types[7]);Memory.Pointer(0x67008,0x68000);Memory.Write(0x67000+297,new byte[]{0});
            Memory.Pointer(0x68000,Types[8]);Memory.Pointer(0x68008,0x60000);SetEngine(0x60000,2);
            Memory.Pointer(Nodes[6]+8,0x67000);
        }
    }
    internal static void Run()
    {
        int checks=0;
        void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
        var fixture=new Fixture();
        Check(fixture.Binding.TryRead(out double rate) && rate==1.75,"A validated graph must read the engine float.");
        fixture.Memory.Pointer(0x70000,fixture.Types[0]);fixture.Memory.Pointer(0x70008,fixture.Nodes[1]);fixture.Memory.Pointer(Fixture.Root,0x70000);
        Check(fixture.Binding.TryRead(out rate) && rate==1.75,"GC relocation must follow the updated root slot, not the old object address.");
        fixture.ReplacePlayer();
        Check(fixture.Binding.TryRead(out rate) && rate==2,"A replacement player must be followed through the current service reference.");
        fixture.Memory.Write(0x67000+297,new byte[]{1});
        Check(!fixture.Binding.TryRead(out _),"Disposed players must not supply a stale speed.");
        fixture.Memory.Write(0x67000+297,new byte[]{0});fixture.Memory.Pointer(fixture.Nodes[6],0x123450);
        Check(!fixture.Binding.TryRead(out _),"A reused reference slot with a different object type must be rejected.");
        fixture.Binding.Dispose();fixture.Binding.Dispose();
        Check(!fixture.Binding.TryRead(out _) && fixture.Memory.DisposeCount==1,"Reader disposal must be final and idempotent.");

        var changing=new Fixture();bool changed=false;
        changing.Memory.BeforeRead=address=>{if(address==Fixture.Engine+0x320 && !changed){changed=true;changing.ReplacePlayer();}};
        Check(!changing.Binding.TryRead(out _),"An owner replacement during the read must reject the mixed snapshot.");
        Check(changing.Binding.TryRead(out rate) && rate==2,"The next consistent snapshot must recover without a new object scan.");
        changing.Binding.Dispose();
        var invalid=new Fixture();invalid.Memory.Write(Fixture.Engine+0x320,BitConverter.GetBytes(float.NaN));
        Check(!invalid.Binding.TryRead(out _),"Non-finite native rates must be rejected.");
        invalid.SetEngine(Fixture.Handle,0);Check(!invalid.Binding.TryRead(out _),"Zero native rates must be rejected.");
        invalid.SetEngine(Fixture.Handle,2);invalid.Memory.Write(Fixture.Handle+0x4000,new byte[]{0x90});
        Check(!invalid.Binding.TryRead(out _),"Unknown getter code must not be interpreted using a known offset.");
        invalid.Memory.Pointer(Fixture.Handle+0x3000+0x50,0xb0000);invalid.Memory.Write(0xb0000,Convert.FromHexString("8B8120030000660F6EC0C3"));
        Check(!invalid.Binding.TryRead(out _),"A lookalike getter outside the identified native module must be rejected.");
        invalid.Binding.Dispose();
        Console.WriteLine($"PASS: {checks} read-only player-rate binding checks for GC relocation, replacement, consistency and rejection.");
    }
}
