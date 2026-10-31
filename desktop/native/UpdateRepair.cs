using System;
using System.IO;
using System.Text;

namespace Popcorn;

internal sealed class UpdateRepair
{
    private readonly string root,directory,version,variant;
    internal static string PathFor(string directory)=>Path.Combine(directory,"repair.txt");
    internal UpdateRepair(string root,string directory,string version,string variant)
    {this.root=root;this.directory=directory;this.version=version;this.variant=variant;}
    private string Header=>"github-direct-repair-v1|"+root.ToUpperInvariant()+"|"+version+"|"+variant;
    internal void Save()
    {
        // Immutable per-operation scope. File identities are journaled incrementally in *.file-id.
        using var output=new FileStream(UpdatePackage.SafeFile(directory,"repair.txt"),FileMode.CreateNew,FileAccess.Write,FileShare.None);
        output.Write(Encoding.UTF8.GetBytes(Header+"\r\n"));output.Flush(true);
    }
    internal void Remove()
    {
        string path=UpdatePackage.SafeFile(directory,"repair.txt");if(!File.Exists(path))return;
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        byte[] raw=UpdatePackage.ReadLimited(input,4096);UpdatePackage.Check(Encoding.UTF8.GetString(raw)==Header+"\r\n","修复记录已改变，未删除。");
        string identity=UpdateFileIdentity.Read(path);input.Dispose();
        UpdateFileIdentity.RemoveMatching(path,identity,new UpdateFile("repair.txt",raw.Length,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw))));
    }
}
