using System;
using System.Windows.Forms;

namespace Popcorn;

internal static class NativeUpdater
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if(args.Length==0)return UpdateWorker.RecoverAsync().GetAwaiter().GetResult();
            if(args.Length==4 && args[0]=="--pipe" && args[2]=="--parent" && int.TryParse(args[3],out int parent))return UpdateWorker.RunAsync(args[1],parent).GetAwaiter().GetResult();
            throw new ArgumentException("更新组件启动参数无效。");
        }
        catch(Exception error)
        {
            Console.Error.WriteLine("Native update operation failed: "+error.GetType().Name);
            if(args.Length==0)MessageBox.Show("更新恢复未完成："+(error is System.IO.InvalidDataException or InvalidOperationException ? error.Message : "请检查文件权限和备份是否完整。")+"\r\n备份已保留，未确认恢复成功。","爆米花弹幕伴侣",MessageBoxButtons.OK,MessageBoxIcon.Error);
            return 1;
        }
    }
}
