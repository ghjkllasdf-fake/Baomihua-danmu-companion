using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;

namespace Popcorn;

// Only manages this application's shortcut in the current user's Startup folder.
public sealed class StartupRegistration
{
    private const string Description="爆米花弹幕伴侣（后台托盘）";
    private readonly string root, shortcutPath;
    public StartupRegistration(string projectRoot,string startupFolder=null)
    {
        root=Path.GetFullPath(projectRoot);
        string folder=startupFolder??Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if(string.IsNullOrWhiteSpace(folder))throw new InvalidOperationException("无法找到当前用户的开机启动文件夹。");
        shortcutPath=Path.Combine(Path.GetFullPath(folder),"爆米花弹幕伴侣.lnk");
    }
    private string Runtime=>Path.Combine(root,"DanmuCompanion.exe");
    private string Arguments=>"";
    private static object Property(object target,string name)=>target.GetType().InvokeMember(name,BindingFlags.GetProperty,null,target,null);
    private static void Put(object target,string name,object value)=>target.GetType().InvokeMember(name,BindingFlags.SetProperty,null,target,new[] {value});
    private T WithShortcut<T>(string path,Func<object,T> action)
    {
        object shell=null,shortcut=null;
        try
        {
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));
            shortcut=shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[] {path});
            return action(shortcut);
        }
        finally
        {
            if(shortcut!=null && Marshal.IsComObject(shortcut))Marshal.FinalReleaseComObject(shortcut);
            if(shell!=null && Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);
        }
    }
    public bool Enabled
    {
        get
        {
            try
            {
                if(!File.Exists(shortcutPath))return false;
                return WithShortcut(shortcutPath,link=>
                    string.Equals(Property(link,"TargetPath") as string,Runtime,StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Property(link,"Arguments") as string,Arguments,StringComparison.Ordinal) &&
                    string.Equals(Property(link,"WorkingDirectory") as string,root,StringComparison.OrdinalIgnoreCase));
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or SecurityException or COMException or TargetInvocationException)
            {throw new InvalidOperationException("无法读取开机启动状态，请检查启动文件夹的访问权限。");}
        }
    }
    public bool SetEnabled(bool enabled)
    {
        string temp=null;
        try
        {
            if(enabled)
            {
                if(!File.Exists(Runtime) || !File.Exists(Path.Combine(root,"assets","app.ico")))
                    throw new InvalidOperationException("请确认程序文件完整，再设置开机启动。");
                if(File.Exists(shortcutPath) && !Enabled && !WithShortcut(shortcutPath,link=>(Property(link,"Description") as string)==Description))
                    throw new InvalidOperationException("开机启动位置已有其他快捷方式，未覆盖原文件。");
                Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));
                temp=Path.Combine(Path.GetDirectoryName(shortcutPath),"爆米花弹幕伴侣-"+Guid.NewGuid().ToString("N")+".lnk");
                WithShortcut(temp,link=>{
                    Put(link,"TargetPath",Runtime);Put(link,"Arguments",Arguments);Put(link,"WorkingDirectory",root);
                    Put(link,"IconLocation",Path.Combine(root,"assets","app.ico")+",0");
                    Put(link,"Description",Description);Put(link,"WindowStyle",7);
                    link.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,link,null);
                    return true;
                });
                File.Move(temp,shortcutPath,true);
            }
            else if(Enabled)File.Delete(shortcutPath);
            bool actual=Enabled;
            if(actual!=enabled)throw new InvalidOperationException("开机启动结果未能确认，请重试。");
            return actual;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or SecurityException or COMException or TargetInvocationException)
        {throw new InvalidOperationException("无法更新开机启动，请检查启动文件夹的访问权限后重试。");}
        finally {if(temp!=null && File.Exists(temp)){try {File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}}
    }
}
