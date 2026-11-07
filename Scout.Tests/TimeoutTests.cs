using System.Net;
using System.Text.Json.Nodes;
using Scout.Core;

static class TimeoutTests
{
    public static void Run(Action<string,Action> test) {
        void Check(bool value){if(!value)throw new Exception("Assertion failed.");}
        test("Real HttpClient deadlines are model timeouts, never caller cancellation",()=>{
            foreach(var provider in new[]{"managed","local","openai","compatible"}) {
                using var handler=new AsyncHandler(async (_,ct)=>{await Task.Delay(Timeout.Infinite,ct);throw new Exception("Unreachable");});
                using var http=new HttpClient(handler){Timeout=TimeSpan.FromMilliseconds(100)};
                using var operation=new CancellationTokenSource();
                var model=new ModelTransport(http,new(){Provider=provider,AllowCloudDialogue=true,ApiKey="fake"});
                var endpoint=provider=="openai"?"https://api.openai.com/v1/responses":provider=="compatible"?"https://example.com/v1/chat/completions":"http://127.0.0.1:1234/v1/chat/completions";
                try{model.CompleteAsync("model",endpoint,"p",new(),new JsonObject(),operation.Token).GetAwaiter().GetResult();throw new Exception("Expected timeout");}
                catch(ModelRequestTimeoutException ex){Check(ex.TimeoutSeconds==0.1&&ex.Message.Contains("timed out")&&!operation.IsCancellationRequested&&model.Usage.Requests==1);}
            }
        });
        test("Explicit model cancellation stays cancellation",()=>{
            using var operation=new CancellationTokenSource();
            using var handler=new AsyncHandler(async (_,ct)=>{operation.Cancel();await Task.Delay(Timeout.Infinite,ct);throw new Exception("Unreachable");});
            using var http=new HttpClient(handler);var model=new ModelTransport(http,new());
            try{model.CompleteAsync("m","http://127.0.0.1:1234/v1/chat/completions","p",new(),new JsonObject(),operation.Token).GetAwaiter().GetResult();throw new Exception("Expected cancellation");}
            catch(OperationCanceledException){Check(operation.IsCancellationRequested);}
        });
        test("Transport abort without caller cancellation is a retryable failure",()=>{
            using var handler=new AsyncHandler((_,_)=>throw new TaskCanceledException("private server details"));
            using var http=new HttpClient(handler);var model=new ModelTransport(http,new());
            try{model.CompleteAsync("m","http://127.0.0.1:1234/v1/chat/completions","p",new(),new JsonObject(),default).GetAwaiter().GetResult();throw new Exception("Expected interruption");}
            catch(IOException ex){Check(ex.Message.Contains("without a user cancellation")&&!ex.Message.Contains("private"));}
        });
        test("Metadata deadlines remain distinct from explicit cancellation",()=>{
            using var delay=new AsyncHandler(async (_,ct)=>{await Task.Delay(Timeout.Infinite,ct);throw new Exception("Unreachable");});
            using var http=new HttpClient(delay){Timeout=TimeSpan.FromMilliseconds(100)};
            try{new MetadataCatalog(http,"tvmaze").SearchAsync("Show",default).GetAwaiter().GetResult();throw new Exception("Expected timeout");}
            catch(TimeoutException ex){Check(ex.Message.Contains("TVMAZE")&&ex.Message.Contains("timed out"));}
            using var operation=new CancellationTokenSource();operation.Cancel();
            try{new MetadataCatalog(http,"tvmaze").SearchAsync("Show",operation.Token).GetAwaiter().GetResult();throw new Exception("Expected cancellation");}
            catch(OperationCanceledException){Check(operation.IsCancellationRequested);}
        });
        test("CPU request minimum is independent of thinking and retains longer requested limits",()=>{
            var s=new AppSettings{Provider="managed",DownloadProfile="gemma4-31b",GpuLayers=0,ThinkingEnabled=false,RequestTimeoutSeconds=600};
            Check(s.EffectiveTimeoutSeconds==1800&&!s.ManagedThinking);
            s.RequestTimeoutSeconds=3600;Check(s.EffectiveTimeoutSeconds==3600);
            s.RequestTimeoutSeconds=600;s.ThinkingEnabled=true;Check(s.EffectiveTimeoutSeconds==7200);
            s.ThinkingEnabled=false;s.GpuLayers=8;Check(s.EffectiveTimeoutSeconds==600);
            foreach(var provider in new[]{"local","compatible","openai"}){s.Provider=provider;s.GpuLayers=0;Check(s.EffectiveTimeoutSeconds==600);}
        });
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>send(request,ct);
    }
}
