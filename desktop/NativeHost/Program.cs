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
        int smoke=0,debugPort=0;string profile="app",dataRoot=null,result=null;bool check=false,open=false,removeStartup=false;
        try
        {
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
                    default:throw new ArgumentException("Unknown application option.");
                }
            }
            if(!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)throw new PlatformNotSupportedException("Windows x64 is required.");
            var paths=new ApplicationPaths(AppContext.BaseDirectory,profile,dataRoot);
            if(removeStartup){new StartupRegistration(paths.Root).RemoveOwnedNative();return 0;}
            NativeRuntime.ValidateApplication(paths);
            NativeRuntime.LoadMediaProjection(paths.Root);
            if(check){Console.WriteLine("PASS: native executable payload and projection dependencies.");return 0;}
            NativeRuntime.ConfigureBrowserEnvironment(paths,debugPort);
            string browserVersion=SharedBrowserRuntime.RequireAvailable();
            using var instance=new ApplicationInstance(paths);
            if(!instance.Acquired)throw new InvalidOperationException("本用户的弹幕伴侣已在运行，请从托盘打开，或先退出旧实例。");
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
            string api=Environment.GetEnvironmentVariable("DANMU_API_BASE");
            api=string.IsNullOrWhiteSpace(api) ? "" : ApiClient.NormalizeBase(api);
            using var window=new DesktopWindow(paths.Root,api,profile,paths.DataRoot){SuppressErrorDialogs=smoke>0};
            using var timer=new System.Windows.Forms.Timer();
            if(smoke>0){timer.Interval=smoke*1000;timer.Tick+=(_,_)=>{timer.Stop();window.RequestExit();};timer.Start();}
            if(open)window.OpenWorkbench();
            Application.Run(window);
            if(result!=null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(result));
                File.WriteAllText(result,JsonSerializer.Serialize(new{window.Ready,window.Failure,Runtime=Environment.Version.ToString(),
                    ApplicationRoot=paths.Root,DataRoot=paths.DataRoot,BrowserMode="evergreen",BrowserVersion=browserVersion},new JsonSerializerOptions{WriteIndented=true}));
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
            if(smoke==0 && !check && !removeStartup)MessageBox.Show("启动失败："+error.Message+"\r\n请完整解压或重新安装本版本，不要从压缩软件中直接运行。", "爆米花弹幕伴侣",MessageBoxButtons.OK,MessageBoxIcon.Error);
            return 1;
        }
    }
}
