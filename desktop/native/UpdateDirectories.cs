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
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeName {internal ushort Length,MaximumLength;internal IntPtr Buffer;}
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes {internal int Length;internal IntPtr RootDirectory,ObjectName;internal uint Attributes;internal IntPtr SecurityDescriptor,SecurityQualityOfService;}
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus {internal IntPtr Status;internal UIntPtr Information;}
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle file,uint access,ref ObjectAttributes attributes,out IoStatus status,IntPtr allocationSize,uint fileAttributes,uint share,uint disposition,uint options,IntPtr ea,uint eaLength);
    private readonly Dictionary<string,SafeFileHandle> handles=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> roots=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> created=new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string,string> createdSink;
    internal Action<string> BeforeCreate;
    internal UpdateDirectories(Action<string,string> createdSink=null){this.createdSink=createdSink;}
    private SafeFileHandle Child(string parent,string name,bool create,out bool owned)
    {
        IntPtr text=Marshal.StringToHGlobalUni(name),unicode=Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeName>());owned=false;
        try
        {
            Marshal.StructureToPtr(new UnicodeName{Length=checked((ushort)(name.Length*2)),MaximumLength=checked((ushort)(name.Length*2)),Buffer=text},unicode,false);
            var attributes=new ObjectAttributes{Length=Marshal.SizeOf<ObjectAttributes>(),RootDirectory=handles[parent].DangerousGetHandle(),ObjectName=unicode,Attributes=0x40};
            uint options=1|0x20|0x200000;int result=NtCreateFile(out var file,create ? 0x110081u : 0x100081u,ref attributes,out var info,IntPtr.Zero,0x80,3,create ? 2u : 1u,options,IntPtr.Zero,0);
            if(create && result==unchecked((int)0xC0000035))
            {file?.Dispose();result=NtCreateFile(out file,0x100081,ref attributes,out info,IntPtr.Zero,0x80,3,1,options,IntPtr.Zero,0);}
            if(result<0){file?.Dispose();throw new IOException("无法锁定更新目录，未继续覆盖。");}
            owned=info.Information.ToUInt64()==2;return file;
        }
        finally{Marshal.FreeHGlobal(unicode);Marshal.FreeHGlobal(text);}
    }
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
        if(create && parent!=null)BeforeCreate?.Invoke(directory);
        bool owned=false;var file=parent==null ? CreateFile(@"\\?\"+directory,0x81,3,IntPtr.Zero,3,0x02000000|0x00200000,IntPtr.Zero) : Child(parent,Path.GetFileName(directory),create,out owned);
        try
        {
            UpdatePackage.Check(!file.IsInvalid,"更新目录被占用或无权限，未继续覆盖。");var info=new byte[8];
            UpdatePackage.Check(GetFileInformationByHandleEx(file,9,info,8) && (BitConverter.ToUInt32(info)&0x410)==0x10,"更新目录含链接或已被替换，未继续覆盖。");
            // Deny directory deletion/rename until all descendant file operations finish.
            handles.Add(directory,file);file=null;
            if(owned)
            {
                created.Add(directory);
                try{createdSink?.Invoke(directory,Identity(directory));}
                catch{SetFileInformationByHandle(handles[directory],4,new byte[]{1},1);throw;}
            }
        }
        finally{file?.Dispose();}
    }
    internal void RemoveEmpty(string directory,string expectedIdentity=null)
    {
        directory=Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if(!handles.TryGetValue(directory,out var held))return;var identity=new byte[24];
        if(!GetFileInformationByHandleEx(held,18,identity,24))return;
        if(expectedIdentity==null || Convert.ToHexString(identity)!=expectedIdentity)return;
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
    internal string Identity(string directory)
    {
        directory=Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));Hold(directory);var identity=new byte[24];
        UpdatePackage.Check(GetFileInformationByHandleEx(handles[directory],18,identity,24),"无法确认应用目录的磁盘身份。");return Convert.ToHexString(identity);
    }
    internal UpdateOwnedDirectory[] Created(string root)
    {
        root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(created,path=>path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)),
            path=>new UpdateOwnedDirectory(Path.GetRelativePath(root,path).Replace(Path.DirectorySeparatorChar,'/'),Identity(path))));
    }
    public void Dispose(){foreach(var file in handles.Values)file.Dispose();handles.Clear();}
}
