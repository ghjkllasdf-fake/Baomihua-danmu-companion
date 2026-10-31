using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Popcorn;

internal sealed record UpdateRetry(DateTimeOffset NotBefore,int Failures,bool ServerLimited);
internal static class UpdateRetryStorage
{
    internal static DateTimeOffset Deadline(DateTimeOffset now,TimeSpan wait)=>
        wait>=DateTimeOffset.MaxValue-now ? DateTimeOffset.MaxValue : now+wait;
    internal static UpdateRetry Load(string path,DateTimeOffset now,out string notice)
    {
        notice="";
        try
        {
            using var doc=UpdateStorage.ReadJson(path,4096);var root=doc.RootElement;
            UpdatePackage.Check(root.EnumerateObject().Count()==4 && root.GetProperty("schemaVersion").GetInt32()==1,"检查退避记录无效。");
            int failures=root.GetProperty("failures").GetInt32();bool limited=root.GetProperty("serverLimited").GetBoolean();string stamp=root.GetProperty("notBefore").GetString();
            UpdatePackage.Check(failures is >=0 and <=3 && stamp!=null && stamp.Length<=64,"检查退避记录无效。");
            if(stamp==""){UpdatePackage.Check(!limited,"检查退避记录无效。");return new(DateTimeOffset.MinValue,failures,false);}
            UpdatePackage.Check(DateTimeOffset.TryParseExact(stamp,"O",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var until),"检查退避期限无效。");
            return until>now ? new(until,failures,limited) : new(DateTimeOffset.MinValue,failures,false);
        }
        catch(Exception error) when(error is FileNotFoundException or DirectoryNotFoundException){return new(DateTimeOffset.MinValue,0,false);}
        catch(Exception error) when(UpdateStorage.MetadataError(error))
        {notice="检查退避记录无法读取，自动检查暂缓六小时；仍可手动检查。";return new(Deadline(now,TimeSpan.FromHours(6)),1,false);}
    }
    internal static void Save(string path,UpdateRetry retry)=>UpdateStorage.Atomic(path,JsonSerializer.SerializeToUtf8Bytes(new{
        schemaVersion=1,notBefore=retry.NotBefore==DateTimeOffset.MinValue ? "" : retry.NotBefore.ToString("O"),failures=retry.Failures,serverLimited=retry.ServerLimited},UpdateStorage.Json));
}
