using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Popcorn;

public static class ProbeJobTests
{
    public static async Task Run(string runtime)
    {
        var info=new ProcessStartInfo(runtime) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true};
        foreach (string value in new[] {"-NoLogo","-NoProfile","-NonInteractive","-MTA","-Command","[Console]::WriteLine('ready'); [Threading.Thread]::Sleep(-1)"}) info.ArgumentList.Add(value);
        using var process=new Process {StartInfo=info};
        using var job=new ProbeJob();
        try
        {
            if (!process.Start()) throw new Exception("Synthetic CLI did not start.");
            job.Assign(process);
            if (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8))!="ready") throw new Exception("Synthetic CLI did not acknowledge initialization.");
            job.Dispose();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (!process.HasExited) throw new Exception("Closing the last owned Job handle did not terminate the owned child.");
            Console.WriteLine("PASS: kill-on-close ends the already-associated synthetic CLI using only the owned Job handle.");
        }
        finally
        {
            if (process.Id>0 && !process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        }
    }
}
