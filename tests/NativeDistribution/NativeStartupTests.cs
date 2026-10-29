using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Popcorn;

internal static class NativeStartupTests
{
    private static object Property(object target,string name)=>target.GetType().InvokeMember(name,BindingFlags.GetProperty,null,target,null);
    private static void Put(object target,string name,object value)=>target.GetType().InvokeMember(name,BindingFlags.SetProperty,null,target,new[]{value});
    private static void Shortcut(string path,Action<object> action)
    {
        object shell=null,link=null;
        try
        {
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));
            link=shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{path});
            action(link);
        }
        finally
        {
            if(link!=null && Marshal.IsComObject(link))Marshal.FinalReleaseComObject(link);
            if(shell!=null && Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);
        }
    }
    internal static void Run()
    {
        string folder=Path.Combine(Path.GetTempPath(),"DanmuNativeStartup-"+Guid.NewGuid().ToString("N"));
        string app=Path.Combine(folder,"\u793a\u4f8b portable"),startup=Path.Combine(folder,"isolated-startup");
        string exe=Path.Combine(app,"DanmuCompanion.exe"),icon=Path.Combine(app,"assets","app.ico");
        Directory.CreateDirectory(Path.GetDirectoryName(icon));
        File.WriteAllText(exe,"Controlled placeholder; never executed.");File.WriteAllText(icon,"Controlled icon-path placeholder.");
        try
        {
            var registration=new StartupRegistration(app,startup);
            DistributionTests.Check(!registration.Enabled && !Directory.Exists(startup),"Reading startup defaults must not create files.");
            DistributionTests.Check(registration.SetEnabled(true) && registration.Enabled,"Explicit startup opt-in must create a working EXE shortcut.");
            string shortcut=Directory.GetFiles(startup,"*.lnk")[0];
            Shortcut(shortcut,link=>
            {
                DistributionTests.Check(string.Equals((string)Property(link,"TargetPath"),exe,StringComparison.OrdinalIgnoreCase),"Startup target must be the main EXE.");
                DistributionTests.Check((string)Property(link,"Arguments")=="","Native startup must not contain shell/script arguments.");
                DistributionTests.Check((string)Property(link,"WorkingDirectory")==app && (int)Property(link,"WindowStyle")==7,"Startup must preserve the chosen path and hidden launch.");
                DistributionTests.Check((string)Property(link,"IconLocation")==icon+",0","Startup icon must belong to this application.");
            });
            DistributionTests.Check(registration.SetEnabled(true) && Directory.GetFiles(startup).Length==1,"Repeated startup opt-in must be atomic/idempotent.");
            var other=new StartupRegistration(Path.Combine(folder,"another-copy"),startup);
            DistributionTests.Check(!other.Enabled,"Another portable folder must not claim ownership of this startup entry.");
            DistributionTests.Check(!other.SetEnabled(false) && File.Exists(shortcut),"One copy must not remove another copy's startup shortcut.");
            DistributionTests.Check(!registration.SetEnabled(false) && !File.Exists(shortcut),"Startup opt-out must remove this exact application's shortcut.");
            DistributionTests.Check(!registration.SetEnabled(false),"Repeated startup opt-out must be safe.");
            Shortcut(shortcut,link=>
            {
                Put(link,"TargetPath",exe);Put(link,"Arguments","--unrelated-sentinel");Put(link,"Description","unrelated sentinel");
                link.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,link,null);
            });
            try{registration.SetEnabled(true);throw new Exception("Unrelated shortcut was overwritten.");}
            catch(InvalidOperationException){}
            DistributionTests.Check(!registration.SetEnabled(false) && File.Exists(shortcut),"Unrelated shortcut must survive opt-out.");
            var incomplete=new StartupRegistration(Path.Combine(folder,"missing"),Path.Combine(folder,"unused-startup"));
            try{incomplete.SetEnabled(true);throw new Exception("Incomplete payload registered startup.");}
            catch(InvalidOperationException error){DistributionTests.Check(!error.Message.Contains(folder),"Startup failure must not reveal private paths.");}
            DistributionTests.Check(!Directory.Exists(Path.Combine(folder,"unused-startup")),"Rejected startup must not create registration folders.");
            string blocked=Path.Combine(folder,"blocked");File.WriteAllText(blocked,"occupied");
            try{new StartupRegistration(app,blocked).SetEnabled(true);throw new Exception("Blocked startup path succeeded.");}
            catch(InvalidOperationException error){DistributionTests.Check(!error.Message.Contains(folder),"Storage failure must be sanitized.");}
        }
        finally{Directory.Delete(folder,true);}
        Console.WriteLine("PASS: native Windows EXE shortcuts, opt-in/out, ownership, idempotency and sanitized storage errors.");
    }
}
