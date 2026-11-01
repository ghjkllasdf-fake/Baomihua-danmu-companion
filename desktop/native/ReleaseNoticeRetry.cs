using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Popcorn;

internal sealed record ReleaseNoticeRetry(DateTimeOffset NotBefore,int Failures,bool ServerLimited,DateTimeOffset AttemptedAt=default,bool UnconfirmedCorruption=false,DateTimeOffset ServerNotBefore=default);
internal static class ReleaseNoticeRetryStorage
{
    internal static DateTimeOffset Deadline(DateTimeOffset now,TimeSpan wait)=>
        wait>=DateTimeOffset.MaxValue-now ? DateTimeOffset.MaxValue : now+wait;
    internal static ReleaseNoticeRetry Load(string path,DateTimeOffset now,out string notice)
    {
        notice="";
        try
        {
            using var doc=ReleaseNoticeStorage.ReadJson(path,4096);var root=doc.RootElement;
            int schema=root.GetProperty("schemaVersion").GetInt32();
            ReleaseNoticeStorage.Check(schema is 1 or 2 or 3 or 4 && root.EnumerateObject().Count()==(schema==1 ? 4 : schema==2 ? 5 : schema==3 ? 6 : 7),"检查退避记录无效。");
            bool unconfirmed=schema>=3 && root.GetProperty("unconfirmedCorruption").GetBoolean();
            if(unconfirmed)notice="检查退避记录曾无法读取，保守等待已保存；成功检查后清除此提示。";
            DateTimeOffset attempted=DateTimeOffset.MinValue;
            if(schema>=2)
            {
                string value=root.GetProperty("attemptedAt").GetString();
                ReleaseNoticeStorage.Check(value!=null && value.Length<=64 && (value=="" || DateTimeOffset.TryParseExact(value,"O",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out attempted)),"检查尝试时间无效。");
            }
            int failures=root.GetProperty("failures").GetInt32();bool limited=root.GetProperty("serverLimited").GetBoolean();string stamp=root.GetProperty("notBefore").GetString();
            ReleaseNoticeStorage.Check(failures is >=0 and <=3 && stamp!=null && stamp.Length<=64,"检查退避记录无效。");
            DateTimeOffset ParseDeadline(string value)
            {
                ReleaseNoticeStorage.Check(value!=null && value.Length<=64,"检查退避期限无效。");if(value=="")return DateTimeOffset.MinValue;
                ReleaseNoticeStorage.Check(DateTimeOffset.TryParseExact(value,"O",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var result),"检查退避期限无效。");return result;
            }
            var until=ParseDeadline(stamp);
            var server=schema==4 ? ParseDeadline(root.GetProperty("serverNotBefore").GetString()) : limited ? until : DateTimeOffset.MinValue;
            ReleaseNoticeStorage.Check(limited==(server!=DateTimeOffset.MinValue) && server<=until,"服务器限流期限无效。");
            return new(until>now ? until : DateTimeOffset.MinValue,failures,server>now,attempted,unconfirmed,server>now ? server : DateTimeOffset.MinValue);
        }
        catch(Exception error) when(error is FileNotFoundException or DirectoryNotFoundException){return new(DateTimeOffset.MinValue,0,false);}
        catch(Exception error) when(ReleaseNoticeStorage.MetadataError(error))
        {notice="检查退避记录无法读取，自动检查暂缓六小时；仍可手动检查。";return new(Deadline(now,TimeSpan.FromHours(6)),1,false,UnconfirmedCorruption:true);}
    }
    internal static void Save(string path,ReleaseNoticeRetry retry)=>ReleaseNoticeStorage.Atomic(path,JsonSerializer.SerializeToUtf8Bytes(new{
        schemaVersion=4,notBefore=retry.NotBefore==DateTimeOffset.MinValue ? "" : retry.NotBefore.ToString("O"),failures=retry.Failures,serverLimited=retry.ServerLimited,
        attemptedAt=retry.AttemptedAt==DateTimeOffset.MinValue ? "" : retry.AttemptedAt.ToString("O"),unconfirmedCorruption=retry.UnconfirmedCorruption,
        serverNotBefore=retry.ServerNotBefore==DateTimeOffset.MinValue ? "" : retry.ServerNotBefore.ToString("O")},ReleaseNoticeStorage.Json));
}
