using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Popcorn;

internal sealed record UpdateJob(string ApplicationRoot,string DataRoot,string Profile,UpdateDownload Download,bool Reopen,string OperationId);

internal static class UpdatePipe
{
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint process);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint process);
    internal static void CheckPeer(PipeStream pipe,int expected,bool server)
    {
        uint id;bool read=server ? GetNamedPipeClientProcessId(pipe.SafePipeHandle,out id) : GetNamedPipeServerProcessId(pipe.SafePipeHandle,out id);
        UpdatePackage.Check(read && id==expected,"更新组件连接的进程身份不匹配。");
    }
    internal static void CheckImage(int process,string expected)
    {
        using var instance=Process.GetProcessById(process);string image=instance.MainModule?.FileName;
        UpdatePackage.Check(image!=null && string.Equals(Path.GetFullPath(image),Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase),"更新组件可执行文件身份不匹配。");
    }
    internal static async Task WriteAsync<T>(Stream stream,T value,CancellationToken token)
    {
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(value,UpdateStorage.Json);UpdatePackage.Check(bytes.Length<=16384,"更新组件消息过大。");
        byte[] header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);await stream.WriteAsync(header,token);await stream.WriteAsync(bytes,token);await stream.FlushAsync(token);
    }
    internal static async Task<JsonDocument> ReadAsync(Stream stream,CancellationToken token)
    {
        byte[] header=new byte[4];await stream.ReadExactlyAsync(header,token);int length=BinaryPrimitives.ReadInt32LittleEndian(header);
        UpdatePackage.Check(length is >0 and <=16384,"更新组件消息长度无效。");byte[] bytes=new byte[length];await stream.ReadExactlyAsync(bytes,token);
        var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=16});UpdatePackage.Unique(document.RootElement);return document;
    }
}
