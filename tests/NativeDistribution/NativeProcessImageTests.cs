using System;
using System.Diagnostics;
using System.IO;

namespace Popcorn;

internal static class NativeProcessImageTests
{
    internal static void Run()
    {
        using(var current=Process.GetCurrentProcess())
            DistributionTests.Check(Path.GetFullPath(ProcessImage.Read(current))==Path.GetFullPath(Environment.ProcessPath),"Held process image must match its actual executable.");
        for(int i=0;i<6;i++)
        {
            var start=new ProcessStartInfo(Environment.ProcessPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true};
            start.ArgumentList.Add("--image-fixture");
            using var child=Process.Start(start)??throw new InvalidOperationException("Image fixture did not start.");
            try
            {
                _=child.Handle;
                DistributionTests.Check(Path.GetFullPath(ProcessImage.Read(child))==Path.GetFullPath(Environment.ProcessPath),"Live owned child image must be identified without waiting for module enumeration.");
                DistributionTests.Check(child.StandardOutput.ReadLine()=="ready","Image fixture must enter its controlled exit gate.");
                child.StandardInput.WriteLine("exit");child.StandardInput.Close();
                DistributionTests.Check(child.WaitForExit(5000),"Image fixture exceeded its bounded exit deadline.");
                try{DistributionTests.Check(Path.GetFullPath(ProcessImage.Read(child))==Path.GetFullPath(Environment.ProcessPath),"A retained image must still identify the same executable.");}
                catch(InvalidOperationException){DistributionTests.Check(child.HasExited,"Only a confirmed exited process may be skipped during observation.");}
            }
            finally{if(!child.HasExited){child.Kill();child.WaitForExit(5000);}}
        }
        Console.WriteLine("PASS: held image lookup for live children and confirmed-exit handling without module enumeration.");
    }
}
