using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

public static class ApiTimeoutTests
{
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    private static ApiClient Client(HttpMessageHandler handler,TimeSpan timeout)
    {
        var client=new ApiClient();
        var field=typeof(ApiClient).GetField("client",BindingFlags.Instance|BindingFlags.NonPublic);
        ((HttpClient)field.GetValue(client)).Dispose();
        field.SetValue(client,new HttpClient(handler) {Timeout=timeout});
        return client;
    }
    public static async Task Run()
    {
        using(var handler=new StalledBodyHandler())
        using(var client=Client(handler,TimeSpan.FromMilliseconds(250)))
        using(var watchdog=new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            var request=client.FetchAsync("http://fixture.invalid/token","comments","fixture",watchdog.Token);
            await handler.Body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            bool timedOut=false;
            try { await request.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch(InvalidOperationException error) { timedOut=error.Message.Contains("超时") && !error.Message.Contains("fixture.invalid"); }
            catch(OperationCanceledException) { }
            Check(timedOut && !watchdog.IsCancellationRequested,"A stalled response body did not honor the configured whole-request timeout.");
            await handler.Body.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        Console.WriteLine("PASS: body read was actually entered and cancelled by the request deadline before the external watchdog.");
        using(var handler=new StalledHeadersHandler())
        using(var client=Client(handler,TimeSpan.FromMilliseconds(250)))
        using(var watchdog=new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            bool timedOut=false;
            try { await client.FetchAsync("http://fixture.invalid/token","search","fixture",watchdog.Token).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch(InvalidOperationException error) { timedOut=error.Message.Contains("超时"); }
            catch(OperationCanceledException) { }
            Check(timedOut && !watchdog.IsCancellationRequested,"Header timeouts must report timeout rather than user cancellation.");
        }
        foreach(var timeout in new[]{TimeSpan.FromSeconds(2),Timeout.InfiniteTimeSpan})
        {
            using var handler=new StalledBodyHandler();
            using var client=Client(handler,timeout);
            using var caller=new CancellationTokenSource();
            var request=client.FetchAsync("http://fixture.invalid/token","comments","fixture",caller.Token);
            await handler.Body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            caller.Cancel();
            bool cancelled=false;
            try { await request.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch(OperationCanceledException) { cancelled=true; }
            catch(InvalidOperationException) { }
            Check(cancelled,"Explicit caller cancellation must remain cancellation, including infinite timeout.");
            await handler.Body.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        Console.WriteLine("PASS: header deadline, explicit cancellation, infinite timeout and body-read cleanup.");
    }
}

internal sealed class StalledBodyHandler : HttpMessageHandler
{
    public readonly WaitingBody Body=new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken stop)
        =>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StreamContent(Body)});
}
internal sealed class StalledHeadersHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken stop)
    {
        await Task.Delay(Timeout.Infinite,stop);
        throw new Exception("Unreachable synthetic header response.");
    }
}
internal sealed class WaitingBody : Stream
{
    public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Finished=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken stop=default)
    {
        Entered.TrySetResult();
        try { await Task.Delay(Timeout.Infinite,stop); return 0; }
        finally { Finished.TrySetResult(); }
    }
    public override bool CanRead=>true;
    public override bool CanSeek=>false;
    public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException();
    public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long value)=>throw new NotSupportedException();
    public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
