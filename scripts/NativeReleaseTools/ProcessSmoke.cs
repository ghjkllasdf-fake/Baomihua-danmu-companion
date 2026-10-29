using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class ProcessSmoke
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct Entry
    {
        public uint Size,Usage,Id;
        public UIntPtr Heap;
        public uint Module,Threads,Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Name;
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint id);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool Process32FirstW(IntPtr snapshot,ref Entry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool Process32NextW(IntPtr snapshot,ref Entry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    private sealed record Owned(int Id,int Parent,long Started,string Image,Process Handle);
    private static Dictionary<int,int> Parents()
    {
        IntPtr snapshot=CreateToolhelp32Snapshot(2,0);
        if(snapshot==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result=new Dictionary<int,int>();var entry=new Entry{Size=(uint)Marshal.SizeOf<Entry>()};
            if(!Process32FirstW(snapshot,ref entry))throw new Win32Exception(Marshal.GetLastWin32Error());
            do{result[(int)entry.Id]=(int)entry.Parent;}while(Process32NextW(snapshot,ref entry));
            return result;
        }
        finally{CloseHandle(snapshot);}
    }
    private static bool Alive(Owned owned)
    {
        try{return !owned.Handle.HasExited && owned.Handle.StartTime.ToUniversalTime().Ticks==owned.Started;}
        catch(InvalidOperationException){return false;}
    }
    private static void Observe(List<Owned> owned)
    {
        var parents=Parents();bool added;
        do
        {
            added=false;
            foreach(var pair in parents)
            {
                if(owned.Any(p=>p.Id==pair.Key && Alive(p)))continue;
                var parent=owned.LastOrDefault(p=>p.Id==pair.Value);
                if(parent==null)continue;
                Process? process=null;
                try
                {
                    process=Process.GetProcessById(pair.Key);
                    long ticks=process.StartTime.ToUniversalTime().Ticks;
                    if(owned.Any(p=>p.Id==pair.Key && p.Started==ticks)){process.Dispose();continue;}
                    if(ticks<parent.Started || (parent.Handle.HasExited && ticks>parent.Handle.ExitTime.ToUniversalTime().Ticks)){process.Dispose();continue;}
                    string image=process.MainModule?.FileName??throw new InvalidDataException("Cannot identify an owned process image.");
                    _=process.Handle;
                    owned.Add(new Owned(pair.Key,pair.Value,ticks,image,process));process=null;added=true;
                }
                catch(ArgumentException){}
                catch(InvalidOperationException){}
                finally{process?.Dispose();}
            }
        }while(added);
    }
    internal static void Run(string app,string data,string report)
    {
        app=Path.GetFullPath(app);data=Path.GetFullPath(data);report=Path.GetFullPath(report);
        if(Directory.Exists(data) || File.Exists(data))throw new IOException("Smoke data root must be new and isolated.");
        Directory.CreateDirectory(data);
        string handshake=Path.Combine(data,"handshake.json");
        var start=new ProcessStartInfo(Path.Combine(app,"DanmuCompanion.exe")){UseShellExecute=false,WorkingDirectory=Environment.GetFolderPath(Environment.SpecialFolder.System)};
        foreach(string value in new[]{"--smoke-seconds","12","--open","--profile","release-qa","--data-root",data,"--result",handshake})start.ArgumentList.Add(value);
        start.Environment["PATH"]=Environment.GetFolderPath(Environment.SpecialFolder.System);
        start.Environment["DOTNET_ROOT"]=Path.Combine(data,"absent-dotnet");
        start.Environment["DOTNET_ROOT_X64"]=start.Environment["DOTNET_ROOT"];
        start.Environment["WEBVIEW2_BROWSER_EXECUTABLE_FOLDER"]=Path.Combine(data,"absent-browser");
        start.Environment["WEBVIEW2_USER_DATA_FOLDER"]=Path.Combine(data,"wrong-browser-profile");
        start.Environment.Remove("DANMU_API_BASE");
        var owned=new List<Owned>();bool passed=false;string? failure=null;JsonElement ready=default;
        try
        {
            var process=Process.Start(start)??throw new IOException("Application did not launch.");
            owned.Add(new Owned(process.Id,Environment.ProcessId,process.StartTime.ToUniversalTime().Ticks,start.FileName,process));
            var watch=Stopwatch.StartNew();
            while(watch.Elapsed<TimeSpan.FromSeconds(35) && !process.HasExited){Observe(owned);Thread.Sleep(100);}
            Observe(owned);
            if(!process.HasExited)throw new TimeoutException("Native application exceeded the smoke deadline.");
            for(int i=0;i<80 && owned.Any(Alive);i++){Observe(owned);Thread.Sleep(100);}
            if(process.ExitCode!=0 || owned.Any(Alive))throw new InvalidDataException("Native application failed or left an owned process running.");
            ready=JsonDocument.Parse(File.ReadAllText(handshake)).RootElement.Clone();
            if(!ready.GetProperty("Ready").GetBoolean() || ready.GetProperty("Runtime").GetString()!="10.0.14")throw new InvalidDataException("Native/React handshake or self-contained runtime failed.");
            if(ready.TryGetProperty("BrowserMode",out var mode) && mode.GetString()=="evergreen")
            {
                string[] roots={Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Microsoft","EdgeWebView","Application"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Microsoft","EdgeWebView","Application"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","EdgeWebView","Application")};
                var browsers=owned.Where(p=>Path.GetFileName(p.Image).Equals("msedgewebview2.exe",StringComparison.OrdinalIgnoreCase)).ToArray();
                string version=ready.GetProperty("BrowserVersion").GetString()??"";
                if(browsers.Length==0 || browsers.Any(p=>!roots.Any(root=>p.Image.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) ||
                    Path.GetFileName(Path.GetDirectoryName(p.Image))!=version))throw new InvalidDataException("Observed browser is not the detected installed Evergreen Runtime.");
                if(Directory.Exists(Path.Combine(app,"WebView2Runtime")))throw new InvalidDataException("Shared Runtime app contains a private browser.");
            }
            else
            {
                string browser=Path.Combine(app,"WebView2Runtime","msedgewebview2.exe");
                if(!owned.Any(p=>string.Equals(p.Image,browser,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("No private WebView2 process observed.");
            }
            if(!owned.Any(p=>string.Equals(p.Image,Path.Combine(app,"DanmuCompanion.Probe.exe"),StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("No native sampling worker observed.");
            if(File.Exists(Path.Combine(app,"api","runtime.json")))
            {
                if(!owned.Any(p=>string.Equals(p.Image,Path.Combine(app,"api","node.exe"),StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("No packaged includeAPI worker observed.");
                if(!ready.TryGetProperty("LocalApi",out var localApi) || !localApi.GetProperty("Enabled").GetBoolean() || localApi.GetProperty("State").GetString()!="ready")
                    throw new InvalidDataException("Packaged includeAPI did not complete its authenticated health check.");
            }
            if(owned.Any(p=>new[]{"pwsh.exe","powershell.exe","cmd.exe","wscript.exe","cscript.exe"}.Contains(Path.GetFileName(p.Image),StringComparer.OrdinalIgnoreCase)))throw new InvalidDataException("Application spawned a script interpreter.");
            passed=true;
        }
        catch(Exception error){failure=error.Message;throw;}
        finally
        {
            // Only held, creation-time-identified test descendants may be stopped.
            foreach(var child in owned.AsEnumerable().Reverse())
            {
                if(Alive(child)){child.Handle.Kill();child.Handle.WaitForExit(5000);}
            }
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report,JsonSerializer.Serialize(new{passed,failure,handshake=ready.ValueKind==JsonValueKind.Undefined ? (object?)null : ready,
                observation="Owned descendant process snapshots at 100ms intervals; not an event-complete process-creation trace.",
                allOwnedExited=owned.All(p=>!Alive(p)),processes=owned.Select(p=>new{p.Id,p.Parent,p.Started,p.Image})},new JsonSerializerOptions{WriteIndented=true}));
            foreach(var child in owned)child.Handle.Dispose();
        }
    }
}
