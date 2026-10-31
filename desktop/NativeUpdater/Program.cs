using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Popcorn;

internal static class NativeUpdater
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding=new System.Text.UTF8Encoding(false);
            if(args.Length==0){MessageBox.Show("此组件由主程序启动，不提供旧版本恢复。更新未完成时，请用同版本完整安装器修复；Portable 若有外来文件冲突，请勿全部覆盖，可完整解压到新的空目录运行。","爆米花弹幕伴侣");return 0;}
            if((args.Length==2 || args.Length==6) && args[0]=="--launch-installed")
            {
                UpdatePackage.Check(args.Length==2 || args[2]=="--profile" && args[4]=="--data-root","安装后启动参数无效。");
                string stage=UpdatePackage.SafeRoot(AppContext.BaseDirectory);var expected=UpdatePackage.Read(stage);
                using var stageGuard=new UpdatePayloadGuard(stage,expected,file=>!file.Path.Contains('/') || file.Path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase));
                var paths=args.Length==2 ? new ApplicationPaths(UpdatePackage.SafeRoot(args[1])) : new ApplicationPaths(UpdatePackage.SafeRoot(args[1]),args[3],UpdatePackage.SafeRoot(args[5]));
                UpdateWorker.LaunchInstalled(paths,expected,stage).GetAwaiter().GetResult();return 0;
            }
            if(args.Length==3 && args[0]=="--install-current")
            {
                string root=UpdatePackage.SafeRoot(args[1]);var stage=new ApplicationPaths(AppContext.BaseDirectory);NativeRuntime.ValidateApplication(stage);
                var incoming=UpdatePackage.Read(stage.Root);
                UpdatePackage.Check(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any() || UpdateIdentity.RegisteredAt(root,incoming.Variant),"非空目录不是本版已登记的安装位置，未覆盖 Portable 或其他文件。");
                using var rootLock=new UpdateRootLock(root);UpdatePackage.Check(rootLock.Acquired,"原程序仍在运行，未覆盖。");
                string directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(root),"direct-update");
                var apply=UpdateApply.PrepareInstall(root,directory,stage.Root);apply.Apply();
                using var output=new FileStream(UpdatePackage.SafeFile(Path.GetDirectoryName(args[2]),Path.GetFileName(args[2])),FileMode.CreateNew,FileAccess.Write,FileShare.None);
                output.Write(System.Text.Encoding.UTF8.GetBytes(directory));output.Flush(true);return 0;
            }
            if(args.Length==3 && args[0]=="--repair-current")
            {
                string root=UpdatePackage.SafeRoot(args[1]);using var rootLock=new UpdateRootLock(root);UpdatePackage.Check(rootLock.Acquired,"原程序仍在运行，未修复。");
                NativeRuntime.ValidateApplication(new ApplicationPaths(AppContext.BaseDirectory));var apply=UpdateApply.Load(args[2],AppContext.BaseDirectory);
                UpdatePackage.Check(string.Equals(apply.Root,root,StringComparison.OrdinalIgnoreCase) && (apply.Previous==null || UpdateIdentity.RegisteredAt(root,apply.Next.Variant)),"修复范围或原安装登记不匹配，未覆盖 Portable 或其他文件。");apply.RepairCurrent();return 0;
            }
            if(args.Length==3 && args[0] is "--verify-current" or "--finish-install")
            {
                var apply=UpdateApply.Load(args[2],AppContext.BaseDirectory);UpdatePackage.Check(string.Equals(apply.Root,UpdatePackage.SafeRoot(args[1]),StringComparison.OrdinalIgnoreCase),"安装验证目录不匹配。");
                apply.AcceptInstalled();
                if(args[0]=="--finish-install"){apply.CompleteMetadata();UpdateStorage.RemoveOwned(UpdateInstalledOwnership.ParentFor(apply.Root),apply.Directory);}
                return 0;
            }
            if(args.Length==1 && args[0]=="--record-installed-ownership")
            {
                var paths=new ApplicationPaths(AppContext.BaseDirectory);var identity=UpdateIdentity.Load(paths);
                UpdatePackage.Check(identity.Supported && identity.Distribution=="installer","安装文件登记仅由当前有效安装执行。");
                var package=UpdatePackage.VerifyDirectory(paths.Root,identity.Version,identity.Variant);
                UpdateInstalledOwnership.Save(paths.Root,package.Owned.Select(file=>new UpdateOwnedFile(file.Path,UpdateFileIdentity.Read(UpdatePackage.SafeFile(paths.Root,file.Path)))).ToArray());
                return 0;
            }
            if(args.Length==2 && args[0]=="--retire-resources")
            {
                var paths=new ApplicationPaths(AppContext.BaseDirectory);var identity=UpdateIdentity.Load(paths);
                UpdatePackage.Check(identity.Supported && identity.Distribution=="installer","旧资源清理仅由当前有效安装执行。");
                return ResourceRetirement.Apply(paths.Root,args[1]);
            }
            if(args.Length==4 && args[0]=="--pipe" && args[2]=="--parent" && int.TryParse(args[3],out int parent))return UpdateWorker.RunAsync(args[1],parent).GetAwaiter().GetResult();
            throw new ArgumentException("更新组件启动参数无效。");
        }
        catch(Exception error)
        {
            Console.Error.WriteLine("Native update operation failed: "+error.GetType().Name);
            return 1;
        }
    }
}
