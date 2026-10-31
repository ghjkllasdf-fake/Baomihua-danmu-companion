using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Popcorn;

internal sealed record UpdateOwnedFile(string Path,string Identity);
internal static class UpdateInstalledOwnership
{
    internal static string PathFor(string root)=>Path.Combine(UpdateTransaction.ParentFor(root),"Owned-Danmu-"+Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(UpdatePackage.SafeRoot(root).ToUpperInvariant())))[..12]+".txt");
    private static string Header(string root)=>"github-updater-owned-v1|"+UpdatePackage.SafeRoot(root).ToUpperInvariant();
    internal static UpdateOwnedFile[] Load(string root,string path=null)
    {
        path??=PathFor(root);if(!File.Exists(path))return Array.Empty<UpdateOwnedFile>();
        UpdatePackage.SafeFile(Path.GetDirectoryName(path),Path.GetFileName(path));using var input=File.OpenRead(path);
        string[] lines=Encoding.UTF8.GetString(UpdatePackage.ReadLimited(input,4*1024*1024)).TrimStart('\ufeff').Split('\n').Select(line=>line.TrimEnd('\r')).Where(line=>line.Length>0).ToArray();
        UpdatePackage.Check(lines.Length is >0 and <=16385 && lines[0].Equals(Header(root),StringComparison.OrdinalIgnoreCase),"安装新增文件登记不属于此目录。");
        var files=lines.Skip(1).Select(line=>
        {
            string[] parts=line.Split('|');UpdatePackage.Check(parts.Length==2,"安装新增文件登记无效。");string relative=parts[0].Replace('\\','/');UpdatePackage.SafeRelative(relative);
            UpdatePackage.Check(parts[1].Length==48 && parts[1].All(char.IsAsciiHexDigit),"安装新增文件身份无效。");return new UpdateOwnedFile(relative,parts[1]);
        }).ToArray();UpdatePackage.Check(files.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()==files.Length,"安装新增文件登记重复。");return files;
    }
    internal static void Save(string root,UpdateOwnedFile[] files,string path=null)
    {
        path??=PathFor(root);if(File.Exists(path))Load(root,path);
        byte[] bytes=Encoding.UTF8.GetBytes(Header(root)+"\r\n"+string.Join("\r\n",files.Select(f=>f.Path.Replace('/','\\')+"|"+f.Identity))+"\r\n");
        UpdatePackage.Check(bytes.Length<=4*1024*1024,"安装新增文件登记超过预算。");UpdateStorage.Atomic(path,bytes);
    }
}
