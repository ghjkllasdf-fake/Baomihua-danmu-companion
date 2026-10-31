using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Popcorn;

internal static class UpdateFileIdentity
{
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file,int informationClass,byte[] information,uint size);
    internal static string Read(string path)
    {
        using var file=File.OpenHandle(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var information=new byte[24];
        UpdatePackage.Check(GetFileInformationByHandleEx(file,18,information,24),"无法确认更新文件的磁盘身份。");
        return Convert.ToHexString(information);
    }
}
