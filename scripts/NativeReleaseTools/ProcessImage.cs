using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class ProcessImage
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref int length);

    internal static string Read(Process process)
    {
        // Avoid module enumeration in an initializing/exiting address space.
        // The held handle fences PID reuse; only a confirmed exit may be skipped.
        var image=new StringBuilder(32768);int length=image.Capacity;
        if(!QueryFullProcessImageName(process.Handle,0,image,ref length))
        {
            int error=Marshal.GetLastWin32Error();
            if(process.HasExited)throw new InvalidOperationException("Owned process exited before image capture.");
            throw new Win32Exception(error);
        }
        string path=image.ToString();
        if(!Path.IsPathFullyQualified(path))throw new InvalidDataException("Cannot identify an owned process image.");
        return path;
    }
}
