using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Popcorn;

internal sealed record UpdateOwnedFile(string Path,string Identity);
internal static class UpdateInstalledOwnership
{
    internal static string ParentFor(string root)=>Path.Combine(Path.GetDirectoryName(UpdatePackage.SafeRoot(root)),".DanmuCompanion-updates");
    internal static string PathFor(string root)=>Path.Combine(ParentFor(root),"Owned-Danmu-"+Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(UpdatePackage.SafeRoot(root).ToUpperInvariant())))[..12]+".txt");
    private static string Header(string root)=>"github-updater-owned-v1|"+UpdatePackage.SafeRoot(root).ToUpperInvariant();
    internal static UpdateOwnedFile[] Load(string root,string path=null)
    {
        path??=PathFor(root);if(!File.Exists(path))return Array.Empty<UpdateOwnedFile>();
        UpdatePackage.SafeFile(Path.GetDirectoryName(path),Path.GetFileName(path));using var input=File.OpenRead(path);
        string[] lines=Encoding.UTF8.GetString(UpdatePackage.ReadLimited(input,4*1024*1024)).TrimStart('\ufeff').Split('\n').Select(line=>line.TrimEnd('\r')).Where(line=>line.Length>0).ToArray();
        UpdatePackage.Check(lines.Length is >0 and <=16386 && lines[0].Equals(Header(root),StringComparison.OrdinalIgnoreCase),"安装新增文件登记不属于此目录。");
        var files=lines.Skip(1).Select(line=>
        {
            string[] parts=line.Split('|');UpdatePackage.Check(parts.Length==2,"安装新增文件登记无效。");string relative=parts[0].Replace('\\','/');UpdatePackage.SafeRelative(relative);
            UpdatePackage.Check(parts[1].Length==48 && parts[1].All(char.IsAsciiHexDigit),"安装新增文件身份无效。");return new UpdateOwnedFile(relative,parts[1]);
        }).ToArray();UpdatePackage.Check(files.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()==files.Length,"安装新增文件登记重复。");return files;
    }
    internal static void Save(string root,UpdateOwnedFile[] files,string path=null,string expectedIdentity=null)
    {
        path??=PathFor(root);UpdatePackage.SafeFile(Path.GetDirectoryName(path),Path.GetFileName(path));
        byte[] previous=null;string identity="";
        if(File.Exists(path))
        {
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);Load(root,path);
            previous=UpdatePackage.ReadLimited(input,4*1024*1024);identity=UpdateFileIdentity.Read(path);
        }
        UpdatePackage.Check(expectedIdentity==null || expectedIdentity==identity,"安装文件身份登记已被外来文件替换，未覆盖。");
        byte[] bytes=Encoding.UTF8.GetBytes(Header(root)+"\r\n"+string.Join("\r\n",files.Select(f=>f.Path.Replace('/','\\')+"|"+f.Identity))+"\r\n");
        UpdatePackage.Check(bytes.Length<=4*1024*1024,"安装新增文件登记超过预算。");Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes);output.Flush(true);}
            if(previous!=null)UpdatePackage.Check(UpdateFileIdentity.RemoveMatching(path,identity,new UpdateFile(Path.GetFileName(path),previous.Length,Convert.ToHexString(SHA256.HashData(previous)))),"安装文件登记被占用或改变，未覆盖。");
            File.Move(temporary,path,false);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    internal static void Probe(string root,string directory)
    {
        string path=PathFor(root);Load(root);
        if(File.Exists(path))UpdatePackage.Check(UpdateFileIdentity.CanReplace(path),"安装文件身份登记不能安全替换，原程序未覆盖。");
        string parent=Path.GetDirectoryName(path);using var directories=new UpdateDirectories();directories.Hold(parent);
        string probe=UpdatePackage.SafeFile(parent,"ownership-write-probe-"+Guid.NewGuid().ToString("N")+".tmp");
        try{using(var output=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.WriteByte(0);output.Flush(true);}}
        finally{if(File.Exists(probe))File.Delete(probe);}
    }
}
