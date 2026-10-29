using System.Reflection;
using System.Runtime.InteropServices;

internal static class ShortcutCheck
{
    internal static void Run(string file,string expected)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Windows shortcuts require Windows.");
        if(!File.Exists(file))throw new FileNotFoundException("Expected application shortcut is missing.");
        object? shell=null,link=null;
        try
        {
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true)!);
            link=shell!.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{Path.GetFullPath(file)});
            object? Get(string name)=>link!.GetType().InvokeMember(name,BindingFlags.GetProperty,null,link,null);
            if(!string.Equals(Get("TargetPath") as string,Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase) ||
                (Get("Arguments") as string)!="" ||
                !string.Equals(Get("WorkingDirectory") as string,Path.GetDirectoryName(Path.GetFullPath(expected)),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Application shortcut does not launch the direct native EXE in its installed directory.");
        }
        finally
        {
            if(link!=null && Marshal.IsComObject(link))Marshal.FinalReleaseComObject(link);
            if(shell!=null && Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);
        }
    }
}
