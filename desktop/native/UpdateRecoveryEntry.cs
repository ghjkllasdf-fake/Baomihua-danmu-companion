using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Popcorn;

internal static class UpdateRecoveryEntry
{
    private const string Description="DanmuCompanion owned update recovery";
    internal static string PathFor(ApplicationPaths paths)=>Path.Combine(UpdateTransaction.ParentFor(paths.Root),"Recover-Danmu-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(UpdatePackage.SafeRoot(paths.Root).ToUpperInvariant())))[..12]+".lnk");
    private static T WithLink<T>(string path,Func<object,T> action)
    {
        object shell=null,link=null;
        try
        {
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));
            link=shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{path});return action(link);
        }
        finally{if(link!=null)Marshal.FinalReleaseComObject(link);if(shell!=null)Marshal.FinalReleaseComObject(shell);}
    }
    private static object Get(object link,string name)=>link.GetType().InvokeMember(name,BindingFlags.GetProperty,null,link,null);
    private static void Put(object link,string name,object value)=>link.GetType().InvokeMember(name,BindingFlags.SetProperty,null,link,new[]{value});
    private static bool Owned(ApplicationPaths paths,object link,string target=null)
    {
        string executable=Get(link,"TargetPath") as string,directory=executable==null ? null : Path.GetDirectoryName(Path.GetDirectoryName(executable));
        return executable!=null && directory!=null && UpdateStorage.Owns(Path.Combine(paths.ProfileRoot,"updates"),directory) &&
            string.Equals(executable,Path.Combine(directory,"helper","DanmuCompanion.Updater.exe"),StringComparison.OrdinalIgnoreCase) &&
            (target==null || string.Equals(executable,target,StringComparison.OrdinalIgnoreCase)) &&
            (Get(link,"Description") as string)==Description && (Get(link,"Arguments") as string)=="";
    }
    internal static void Create(ApplicationPaths paths,string helper)
    {
        string path=PathFor(paths);UpdatePackage.SafeFile(UpdateTransaction.ParentFor(paths.Root),Path.GetFileName(path));Directory.CreateDirectory(Path.GetDirectoryName(path));
        UpdatePackage.Check(!File.Exists(path) || WithLink(path,link=>Owned(paths,link)),"恢复入口与已有快捷方式冲突，未覆盖。");
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".lnk";
        try
        {
            WithLink(temporary,link=>{Put(link,"TargetPath",helper);Put(link,"WorkingDirectory",Path.GetDirectoryName(helper));Put(link,"Arguments","");Put(link,"Description",Description);Put(link,"WindowStyle",7);link.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,link,null);return true;});
            File.Move(temporary,path,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    internal static void Remove(ApplicationPaths paths,string helper)
    {
        string path=PathFor(paths);if(File.Exists(path) && WithLink(path,link=>Owned(paths,link,helper)))File.Delete(path);
    }
}
