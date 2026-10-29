using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using StorageFile=System.IO.File;

namespace Popcorn;

public sealed record ApiPreferenceReadResult(string ApiBase,bool Saved,string Notice);

public static class ApiPreferences
{
    private const int MaxBytes=16384;
    private static readonly byte[] Entropy=Encoding.UTF8.GetBytes("BaomihuaDanmuCompanion.Api.v1");
    public static ApiPreferenceReadResult Load(string path)
    {
        byte[] clear=null;
        try
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            byte[] encrypted=new byte[MaxBytes+1];int count=0,read;
            while(count<encrypted.Length && (read=stream.Read(encrypted,count,encrypted.Length-count))>0)count+=read;
            if(count==0 || count>MaxBytes)throw new InvalidDataException();
            clear=ProtectedData.Unprotect(encrypted.AsSpan(0,count).ToArray(),Entropy,DataProtectionScope.CurrentUser);
            string value=ApiClient.NormalizeBase(new UTF8Encoding(false,true).GetString(clear));
            return new(value,true,"");
        }
        catch(Exception ex) when(ex is FileNotFoundException or DirectoryNotFoundException){return new("",false,"");}
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {return new("",false,"已保存的 API 地址无法读取，请重新输入并保存；原文件未修改。");}
        finally{if(clear!=null)CryptographicOperations.ZeroMemory(clear);}
    }
    public static void Save(string path,string value)
    {
        string normalized=string.IsNullOrWhiteSpace(value) ? "" : ApiClient.NormalizeBase(value);
        if(normalized.Length==0){StorageFile.Delete(path);return;}
        byte[] clear=Encoding.UTF8.GetBytes(normalized);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            byte[] encrypted=ProtectedData.Protect(clear,Entropy,DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {output.Write(encrypted);output.Flush(true);}
            StorageFile.Move(temporary,path,true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if(StorageFile.Exists(temporary))StorageFile.Delete(temporary);
        }
    }
}
