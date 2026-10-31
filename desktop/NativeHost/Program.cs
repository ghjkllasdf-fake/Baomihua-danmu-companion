using System;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace Popcorn;

internal static class NativeHost
{
    [STAThread]
    private static int Main(string[] args)
    {
        int smoke=0,debugPort=0,updateParent=0;string profile="app",dataRoot=null,result=null,updateHandshake=null;bool check=false,open=false,removeStartup=false;
        try
        {
            Console.OutputEncoding=new System.Text.UTF8Encoding(false);
            for(int i=0;i<args.Length;i++)
            {
                string Value()=>++i<args.Length ? args[i] : throw new ArgumentException("Missing option value.");
                switch(args[i])
                {
                    case "--check":check=true;break;
                    case "--open":open=true;break;
                    case "--remove-startup":removeStartup=true;break;
                    case "--smoke-seconds":smoke=int.Parse(Value());if(smoke is <1 or >120)throw new ArgumentException("Smoke duration must be 1-120 seconds.");break;
                    case "--debug-port":debugPort=int.Parse(Value());if(debugPort is <1 or >65535)throw new ArgumentException("Invalid debug port.");break;
                    case "--profile":profile=Value();break;
                    case "--data-root":dataRoot=Value();break;
                    case "--result":result=Value();if(!Path.IsPathFullyQualified(result))throw new ArgumentException("Result path must be absolute.");break;
                    case "--update-handshake":updateHandshake=Value();if(!Guid.TryParseExact(updateHandshake,"N",out _))throw new ArgumentException("Invalid update operation.");break;
                    case "--update-parent":updateParent=int.Parse(Value());if(updateParent<=0)throw new ArgumentException("Invalid update process.");break;
                    default:throw new ArgumentException("Unknown application option.");
                }
            }
            if(!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)throw new PlatformNotSupportedException("Windows x64 is required.");
            if((updateHandshake==null)!=(updateParent==0))throw new ArgumentException("Incomplete update handshake.");
            var paths=new ApplicationPaths(AppContext.BaseDirectory,profile,dataRoot);
            if(removeStartup){new StartupRegistration(paths.Root).RemoveOwnedNative();return 0;}
            if(smoke==0 && !check && updateHandshake==null)UpdateLegacyEntry.Retire(paths);
            using var rootLock=new UpdateRootLock(paths.Root);
            if(!rootLock.Acquired)throw new InvalidOperationException("此目录中的弹幕伴侣正在运行或更新，请先退出该实例。");
            NativeRuntime.ValidateApplication(paths);
            if(smoke==0 && !check && updateHandshake==null)
            {
                try{if(UpdateIdentity.DistributionFor(paths.Root,Directory.Exists(Path.Combine(paths.Root,"api")) ? "includeAPI" : "standard")=="portable")UpdateRegistration.InitializePortable(paths.Root);}
                catch(Exception error) when(UpdateStorage.MetadataError(error)){Console.Error.WriteLine("Update registration unavailable: "+error.GetType().Name);}
            }
            NativeRuntime.LoadMediaProjection(paths.Root);
            if(check){Console.WriteLine("PASS: native executable payload and projection dependencies.");return 0;}
            NativeRuntime.ConfigureBrowserEnvironment(paths,debugPort);
            SharedBrowserRuntime.ConfigureLoader(paths.Root);
            string browserVersion=SharedBrowserRuntime.RequireAvailable();
            using var instance=new ApplicationInstance(paths);
            if(!instance.Acquired)throw new InvalidOperationException("本用户的弹幕伴侣已在运行，请从托盘打开，或先退出旧实例。");
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
            string api=EmbeddedApiService.IsPackaged(paths) ? "" : Environment.GetEnvironmentVariable("DANMU_API_BASE");
            api=string.IsNullOrWhiteSpace(api) ? "" : ApiClient.NormalizeBase(api);
            using var window=new DesktopWindow(paths.Root,api,profile,paths.DataRoot){SuppressErrorDialogs=smoke>0 || updateHandshake!=null};
            using var timer=new System.Windows.Forms.Timer();
            using var updateTimer=new System.Windows.Forms.Timer{Interval=250};
            if(updateHandshake!=null)
            {
                updateTimer.Tick+=async (_,_)=>{if(!window.UpdateHealthReady)return;updateTimer.Stop();try{await UpdateWorker.WriteStartedAsync(paths,updateHandshake,updateParent);}catch(Exception error){Console.Error.WriteLine("Startup handshake failed: "+error.GetType().Name+" "+(UpdateStorage.MetadataError(error) ? error.Message : "native pipe/process check failed"));window.RequestExit();}};updateTimer.Start();
            }
            if(smoke>0){timer.Interval=smoke*1000;timer.Tick+=(_,_)=>{timer.Stop();window.RequestExit();};timer.Start();}
            if(open)window.OpenWorkbench();
            Application.Run(window);
            if(result!=null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(result));
                File.WriteAllText(result,JsonSerializer.Serialize(new{window.Ready,window.Failure,Runtime=Environment.Version.ToString(),
                    ApplicationRoot=paths.Root,DataRoot=paths.DataRoot,BrowserMode="evergreen",BrowserVersion=browserVersion,LocalApi=window.LocalApiDiagnostics},new JsonSerializerOptions{WriteIndented=true}));
            }
            return smoke>0 && !window.Ready ? 1 : 0;
        }
        catch(Exception error)
        {
            if(result!=null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(result));
                File.WriteAllText(result,JsonSerializer.Serialize(new{Ready=false,Failure=error.Message}));
            }
            Console.Error.WriteLine("Native startup failed: "+error.Message);
            if(smoke==0 && !check && !removeStartup && updateHandshake==null)MessageBox.Show("启动失败："+error.Message+"\r\n请完整解压或重新安装本版本，不要从压缩软件中直接运行。", "爆米花弹幕伴侣",MessageBoxButtons.OK,MessageBoxIcon.Error);
            return 1;
        }
    }
}
