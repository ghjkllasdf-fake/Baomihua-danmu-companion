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
        using var file=OpenGuard(path);
        return Read(file);
    }
    internal static string Read(FileStream file)
    {
        var information=new byte[24];
        UpdatePackage.Check(GetFileInformationByHandleEx(file.SafeFileHandle,18,information,24),"无法确认更新文件的磁盘身份。");
        return Convert.ToHexString(information);
    }
    internal static bool RemoveMatching(string path,string identity,UpdateFile expected=null)
    {
        // Compare and delete through one non-renamable handle, never a successor at the path.
        using var file=CreateFile(@"\\?\"+Path.GetFullPath(path),0x10000|0x80|(expected==null ? 0u : 0x80000000u),expected==null ? 3u : 1u,IntPtr.Zero,3,0x200000,IntPtr.Zero);
        if(file.IsInvalid)return false;
        var information=new byte[24];
        if(!GetFileInformationByHandleEx(file,18,information,24) || Convert.ToHexString(information)!=identity)return false;
        if(expected!=null)
        {
            // Exclude writers while verifying and marking this exact old resource for deletion.
            using var input=new FileStream(file,FileAccess.Read);
            if(input.Length!=expected.Length || !UpdatePackage.Hash(input,expected.Length).Equals(expected.Sha256,StringComparison.OrdinalIgnoreCase))return false;
            return SetFileInformationByHandle(file,4,new byte[]{1},1);
        }
        return SetFileInformationByHandle(file,4,new byte[]{1},1);
    }
    internal static bool CanReplace(string path)
    {
        using var file=CreateFile(@"\\?\"+Path.GetFullPath(path),0x10000|0x80000000,1,IntPtr.Zero,3,0x200000,IntPtr.Zero);
        return !file.IsInvalid;
    }
    internal static FileStream OpenGuard(string path)
    {
        var file=CreateFile(@"\\?\"+Path.GetFullPath(path),0x80000000,1,IntPtr.Zero,3,0x200000,IntPtr.Zero);
        try
        {
            UpdatePackage.Check(!file.IsInvalid,"无法锁定将运行的更新文件。");var attributes=new byte[8];
            UpdatePackage.Check(GetFileInformationByHandleEx(file,9,attributes,8) && (BitConverter.ToUInt32(attributes)&0x410)==0,"将运行的更新文件是链接或目录，未执行。");
            var input=new FileStream(file,FileAccess.Read);file=null;return input;
        }
        finally{file?.Dispose();}
    }
}
