using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Popcorn;

internal sealed class ProbeJob : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
    {
        public long ProcessTime,JobTime;
        public uint Flags;
        public UIntPtr MinWorkingSet,MaxWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint PriorityClass,SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;
    }
    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle():base(true) { }
        protected override bool ReleaseHandle()=>CloseHandle(handle);
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern JobHandle CreateJobObject(IntPtr attributes,string name);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetInformationJobObject(JobHandle job,int kind,ref ExtendedLimit limits,uint length);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool AssignProcessToJobObject(JobHandle job,IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    private readonly JobHandle job;
    public ProbeJob()
    {
        job=CreateJobObject(IntPtr.Zero,null); // Unnamed and non-inheritable; the parent owns the only handle.
        if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var limits=new ExtendedLimit { Basic=new BasicLimit {Flags=0x2000} };
        if (!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimit>()))
        {
            int error=Marshal.GetLastWin32Error(); job.Dispose(); throw new Win32Exception(error);
        }
    }
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(job,process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose()=>job.Dispose();
}
