using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Popcorn;

internal static class UpdateFileIdentity
{
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file,int informationClass,byte[] information,uint size);
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file,int informationClass,byte[] information,uint size);
    internal static string Read(string path)
    {
        using var file=File.OpenHandle(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var information=new byte[24];
        UpdatePackage.Check(GetFileInformationByHandleEx(file,18,information,24),"无法确认更新文件的磁盘身份。");
        return Convert.ToHexString(information);
    }
    internal static bool RemoveMatching(string path,string identity)
    {
        // Compare and delete through one non-renamable handle, never a successor at the path.
        using var file=CreateFile(@"\\?\"+Path.GetFullPath(path),0x10000|0x80,3,IntPtr.Zero,3,0x200000,IntPtr.Zero);
        if(file.IsInvalid)return false;
        var information=new byte[24];
        if(!GetFileInformationByHandleEx(file,18,information,24) || Convert.ToHexString(information)!=identity)return false;
        return SetFileInformationByHandle(file,4,new byte[]{1},1);
    }
}
