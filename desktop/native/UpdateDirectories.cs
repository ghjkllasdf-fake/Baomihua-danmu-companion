using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Popcorn;

internal sealed class UpdateDirectories : IDisposable
{
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file,int informationClass,byte[] information,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file,int informationClass,byte[] information,uint size);
    private readonly Dictionary<string,SafeFileHandle> handles=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> roots=new(StringComparer.OrdinalIgnoreCase);
    internal string File(string root,string relative,bool create=false)
    {
        UpdatePackage.Check(Path.IsPathFullyQualified(root),"更新目录必须是绝对路径。");root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if(!roots.Contains(root)){UpdatePackage.SafeRoot(root);roots.Add(root);}
        UpdatePackage.SafeRelative(relative);string path=Path.GetFullPath(Path.Combine(root,relative));
        UpdatePackage.Check(path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"更新文件越过受控目录。");
        string parent=Path.GetDirectoryName(path);
        if(create)Hold(parent,true);
        else
        {
            while(!Directory.Exists(parent))parent=Path.GetDirectoryName(parent)??throw new InvalidDataException("更新目录没有有效父路径。");
            Hold(parent);
        }
        return path;
    }
    internal void Hold(string directory,bool create=false)
    {
        directory=Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));if(handles.ContainsKey(directory))return;
        string parent=Path.GetDirectoryName(directory);if(parent!=null && !parent.Equals(directory,StringComparison.OrdinalIgnoreCase))Hold(parent,create);
        if(create && !Directory.Exists(directory))Directory.CreateDirectory(directory);
        var file=CreateFile(@"\\?\"+directory,0x81,3,IntPtr.Zero,3,0x02000000|0x00200000,IntPtr.Zero);
        try
        {
            UpdatePackage.Check(!file.IsInvalid,"更新目录被占用或无权限，未继续覆盖。");var info=new byte[8];
            UpdatePackage.Check(GetFileInformationByHandleEx(file,9,info,8) && (BitConverter.ToUInt32(info)&0x410)==0x10,"更新目录含链接或已被替换，未继续覆盖。");
            // Deny directory deletion/rename until all descendant file operations finish.
            handles.Add(directory,file);file=null;
        }
        finally{file?.Dispose();}
    }
    internal void RemoveEmpty(string directory)
    {
        if(!handles.TryGetValue(directory,out var held))return;var identity=new byte[24];
        if(!GetFileInformationByHandleEx(held,18,identity,24))return;
        held.Dispose();handles.Remove(directory);
        using var file=CreateFile(@"\\?\"+directory,0x10000|0x80,3,IntPtr.Zero,3,0x02000000|0x00200000,IntPtr.Zero);
        if(file.IsInvalid)return;var actual=new byte[24];var attributes=new byte[8];
        if(!GetFileInformationByHandleEx(file,18,actual,24) || !actual.AsSpan().SequenceEqual(identity) ||
            !GetFileInformationByHandleEx(file,9,attributes,8) || (BitConverter.ToUInt32(attributes)&0x410)!=0x10)return;
        try
        {
            if(!System.Linq.Enumerable.Any(Directory.EnumerateFileSystemEntries(directory)))SetFileInformationByHandle(file,4,new byte[]{1},1);
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
    public void Dispose(){foreach(var file in handles.Values)file.Dispose();handles.Clear();}
}
