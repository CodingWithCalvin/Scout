using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
static class SettingsTests
{
 public static void Run(Action<string,Action> test,string root) {
  void Check(bool value){if(!value)throw new Exception("Assertion failed.");}
  void Reject(Action action){try{action();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}catch(InvalidDataException){return;}throw new Exception("Expected rejection.");}
  test("Managed Gemma uses thinking and benchmark limits without affecting other providers",()=>{
   var profile=ModelDownloads.Profiles.Single(p=>p.Id=="gemma4-31b");
   Check(profile.EnableThinking&&profile.Bytes==18323733440&&profile.Sha256.Length==64);
   var s=new AppSettings{Provider="managed",DownloadProfile=profile.Id};
   Check(s.ManagedThinking&&s.Inference().EnableThinking&&s.EffectiveOutputTokens==8192&&s.EffectiveContextSize==32768&&s.EffectiveTimeoutSeconds==7200);
   Check(RuntimeDownloads.Profile(s).Id.EndsWith("-cpu"));s.GpuLayers=8;Check(RuntimeDownloads.Profile(s).Id.EndsWith("-vulkan"));
   s.ModelFile=Path.Combine(root,ModelDownloads.Profiles[0].FileName);Check(!s.ManagedThinking);
   s.ModelFile=Path.Combine(root,profile.FileName);s.DownloadProfile="qwen36-35b";Check(s.ManagedThinking);
   s.Provider="local";Check(!s.ManagedThinking&&s.EffectiveOutputTokens==4096&&s.EffectiveTimeoutSeconds==600);
   s.Provider="openai";Check(!s.Inference().EnableThinking);
  });
  test("Explicit thinking override controls managed requests and resource limits",()=>{
   var s=new AppSettings{Provider="managed",DownloadProfile="gemma4-31b",ThinkingEnabled=false};
   Check(!s.ManagedThinking&&!s.Inference().EnableThinking&&s.EffectiveContextSize==24576&&s.EffectiveOutputTokens==4096&&s.EffectiveTimeoutSeconds==1800);
   s.ContextSize=65536;s.MaxOutputTokens=16384;s.RequestTimeoutSeconds=1800;
   Check(s.EffectiveContextSize==65536&&s.EffectiveOutputTokens==16384&&s.EffectiveTimeoutSeconds==1800);
   s.DownloadProfile="qwen36-35b";s.ThinkingEnabled=true;
   Check(s.ManagedThinking&&s.Inference().EnableThinking&&s.EffectiveContextSize==65536&&s.EffectiveOutputTokens==16384&&s.EffectiveTimeoutSeconds==7200);
   foreach(var provider in new[]{"local","compatible","openai"}){s.Provider=provider;Check(!s.ManagedThinking&&!s.Inference().EnableThinking);}
  });
  test("Thinking override persists including explicit false and legacy model defaults",()=>{
   var store=new SettingsStore(Path.Combine(root,"thinking-prefs"));
   foreach(bool? enabled in new bool?[]{false,true,null}) {
    store.Save(new AppSettings{Provider="managed",DownloadProfile="gemma4-31b",ThinkingEnabled=enabled,ModelDirectory=root});
    var saved=store.Load();Check(saved.ThinkingEnabled==enabled&&saved.ManagedThinking==(enabled??true));
   }
   var legacy=JsonSerializer.Deserialize<AppSettings>("""{"provider":"managed","download_profile":"gemma4-31b"}""",Data.Options)!;
   Check(legacy.ThinkingEnabled==null&&legacy.ManagedThinking);
  });
  test("Saved CPU-only thinking-disabled setup drives request payload and cache identity",()=>{
   var store=new SettingsStore(Path.Combine(root,"cpu-prefs"));
   store.Save(new AppSettings{Provider="managed",DownloadProfile="gemma4-31b",GpuLayers=0,ThinkingEnabled=false,ModelDirectory=root});
   var s=store.Load();Check(RuntimeDownloads.Profile(s).Id.EndsWith("-cpu"));
   var handler=new TestHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"{}"}}]}""")});
   using var http=new HttpClient(handler);var transport=new ModelTransport(http,s.Inference());
   transport.CompleteAsync("m",s.Endpoint,"p",new(),new JsonObject(),default).GetAwaiter().GetResult();
   Check(!handler.Requests.Single()["chat_template_kwargs"].Bool("enable_thinking"));
   s.ThinkingEnabled=true;Check(transport.CacheScope!=new ModelTransport(http,s.Inference()).CacheScope);
  });
  test("Settings defaults are local and offline",()=>{var s=new AppSettings();Check(s.Provider=="local"&&!s.AllowCloudDialogue&&!s.AllowDownloads&&!s.AutoDownloadMissing);s.Validate();});
  test("Settings persist without credentials",()=>{
   var dir=Path.Combine(root,"prefs");var store=new SettingsStore(dir);var s=new AppSettings{CpuThreads=4,ModelDirectory=root};store.Save(s);Check(store.Load().CpuThreads==4);
   Check(!File.ReadAllText(Path.Combine(dir,"settings.json")).Contains("api_key",StringComparison.OrdinalIgnoreCase));
  });
  test("Windows credential protection and endpoint isolation",()=>{
   var dir=Path.Combine(root,"secrets");var store=new SettingsStore(dir);store.SetKey("openai|a","fake-secret-12345");
   Check(store.ReadKey("openai|a")=="fake-secret-12345"&&store.ReadKey("compatible|a")==null);
   Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir,"credentials.dpapi"))).Contains("fake-secret"));
   store.SetKey("compatible|b","different");store.SetKey("openai|a",null);Check(store.ReadKey("openai|a")==null&&store.ReadKey("compatible|b")=="different");
  });
  test("Invalid resource limits rejected",()=>{Reject(()=>new AppSettings{CpuThreads=0}.Validate());Reject(()=>new AppSettings{MaxRequestsPerRun=1}.Validate());Reject(()=>new AppSettings{AutoDownloadMissing=true}.Validate());});
  test("Cloud requires explicit opt-in and credentials",()=>{
   Reject(()=>ConnectionPolicy.Validate("openai","https://api.openai.com/v1/responses",false,"fake"));
   Reject(()=>ConnectionPolicy.Validate("openai","https://api.openai.com/v1/responses",true,null));
   Reject(()=>ConnectionPolicy.Validate("openai","https://example.com/v1/responses",true,"fake"));
   Reject(()=>ConnectionPolicy.Validate("compatible","http://example.com/api",true,"fake"));
   Reject(()=>ConnectionPolicy.CloudUri("https://example.com/api?key=secret"));
  });
  test("Local mode refuses remote endpoint",()=>Reject(()=>ConnectionPolicy.Validate("local","https://example.com/api",true,"fake")));
  test("Download and experimental policies enforced",()=>{
   var p=ModelDownloads.Profiles[0];Reject(()=>ModelDownloads.CheckPolicy(p,new()));
   Reject(()=>ModelDownloads.CheckPolicy(p,new(){AllowDownloads=true}));
   ModelDownloads.CheckPolicy(p,new(){AllowDownloads=true,AllowExperimental=true});
  });
  test("Verified model download and existing-file reuse",()=>{
   byte[] bytes=Encoding.UTF8.GetBytes("model fixture");
   var profile=new ModelProfile("test","Test","fixture.gguf",bytes.Length,Convert.ToHexStringLower(SHA256.HashData(bytes)),"https://example.com/model",true,"test");
   var settings=new AppSettings{AllowDownloads=true,ModelDirectory=Path.Combine(root,"models")};
   using var http=new HttpClient(new TestHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)}));
   var path=ModelDownloads.DownloadAsync(profile,settings,http,new Progress<string>(),default).GetAwaiter().GetResult();
   Check(File.ReadAllBytes(path).SequenceEqual(bytes));
   using var offline=new HttpClient(new TestHandler(_=>throw new Exception("Unexpected request")));
   Check(ModelDownloads.DownloadAsync(profile,settings,offline,new Progress<string>(),default).GetAwaiter().GetResult()==path);
  });
  test("Bad model checksum never installed",()=>{
   byte[] bytes=Encoding.UTF8.GetBytes("bad");
   var profile=new ModelProfile("bad","Bad","bad.gguf",bytes.Length,new string('0',64),"https://example.com/model",true,"test");
   var folder=Path.Combine(root,"badmodels");var settings=new AppSettings{AllowDownloads=true,ModelDirectory=folder};
   using var http=new HttpClient(new TestHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)}));
   bool failed=false;try{ModelDownloads.DownloadAsync(profile,settings,http,new Progress<string>(),default).GetAwaiter().GetResult();}catch(IOException){failed=true;}
   Check(failed&&!Directory.EnumerateFiles(folder).Any());
  });
  test("OpenAI request schema, credentials, token usage and cap",()=>{
   var handler=new TestHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"{}"}]}],"usage":{"input_tokens":20,"output_tokens":10}}""")});
   using var client=new HttpClient(handler);var transport=new ModelTransport(client,new(){Provider="openai",AllowCloudDialogue=true,ApiKey="test-key",MaxOutputTokens=4096,MaxRequests=2});
   for(int i=0;i<2;i++)transport.CompleteAsync("test-model","https://api.openai.com/v1/responses","prompt",new(),new JsonObject{["type"]="object"},default).GetAwaiter().GetResult();
   Reject(()=>transport.CompleteAsync("test-model","https://api.openai.com/v1/responses","prompt",new(),new JsonObject(),default).GetAwaiter().GetResult());
   Check(handler.Requests.Count==2&&handler.Authorization=="Bearer test-key");
   var payload=handler.Requests[0];Check(payload["store"]!.GetValue<bool>()==false&&payload["text"]?["format"]?["type"]?.ToString()=="json_schema"&&payload.Int("max_output_tokens")==4096&&!payload.ContainsKey("temperature"));
   Check(transport.Usage.InputTokens==40&&transport.Usage.OutputTokens==20&&transport.Usage.Requests==2);
  });
  test("Opt-out makes zero HTTP requests",()=>{
   var handler=new TestHandler(_=>throw new Exception("Must not send"));using var http=new HttpClient(handler);
   var transport=new ModelTransport(http,new(){Provider="openai",ApiKey="test-key"});
   Reject(()=>transport.CompleteAsync("m","https://api.openai.com/v1/responses","p",new(),new JsonObject(),default).GetAwaiter().GetResult());Check(handler.Requests.Count==0);
  });
  test("Thinking benchmark option changes local payload and cache identity without changing defaults",()=>{
   var handler=new TestHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"{}","reasoning_content":"separate thought"}}]}""")});
   using var http=new HttpClient(handler);
   var normal=new ModelTransport(http,new(){Provider="local"});
   var thinking=new ModelTransport(http,new(){Provider="local",EnableThinking=true});
   normal.CompleteAsync("m","http://127.0.0.1:1234/v1/chat/completions","p",new(),new JsonObject(),default).GetAwaiter().GetResult();
   thinking.CompleteAsync("m","http://127.0.0.1:1234/v1/chat/completions","p",new(),new JsonObject(),default).GetAwaiter().GetResult();
   Check(!handler.Requests[0]["chat_template_kwargs"].Bool("enable_thinking")&&handler.Requests[1]["chat_template_kwargs"].Bool("enable_thinking"));
   Check(normal.CacheScope!=thinking.CacheScope);
  });
  test("Local request never sends API credential",()=>{
   var handler=new TestHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"{}"}}]}""")});
   using var http=new HttpClient(handler);var transport=new ModelTransport(http,new(){Provider="local",ApiKey="not-for-local"});
   transport.CompleteAsync("local","http://127.0.0.1:1234/v1/chat/completions","p",new(),new JsonObject(),default).GetAwaiter().GetResult();
   Check(handler.Authorization==null&&handler.Requests[0].ContainsKey("chat_template_kwargs"));
  });
  test("Model rejection diagnostics distinguish context schema auth and unknown errors without leaking text",()=>{
   var context=ModelRequestError.Create(HttpStatusCode.BadRequest,"""{"error":{"type":"exceed_context_size_error","message":"private dialogue and secret","n_prompt_tokens":25000,"n_ctx":24576}}""",90000,4096);
   Check(context is ModelContextException&&context.Message.Contains("25000")&&context.Message.Contains("24576")&&!context.Message.Contains("private")&&!context.Message.Contains("secret"));
   var schema=ModelRequestError.Create(HttpStatusCode.BadRequest,"""{"error":{"message":"Invalid JSON schema: secret"}}""",1,512);
   Check(schema is not ModelContextException&&schema.Message.Contains("schema")&&!schema.Message.Contains("secret"));
   foreach(var body in new[]{"<html>secret</html>","[]","{\"error\":[]}"})Check(ModelRequestError.Create(HttpStatusCode.BadRequest,body,1,512) is not ModelContextException);
   Check(ModelRequestError.Create(HttpStatusCode.Unauthorized,"secret",1,512).Message.Contains("authentication"));
  });
  test("Provider HTTP errors do not expose response body",()=>{
   var handler=new TestHandler(_=>new HttpResponseMessage(HttpStatusCode.Unauthorized){Content=new StringContent("sensitive echoed content")});
   using var http=new HttpClient(handler);var transport=new ModelTransport(http,new(){Provider="compatible",AllowCloudDialogue=true,ApiKey="secret"});
   try{transport.CompleteAsync("m","https://example.com/v1/chat/completions","p",new(),new JsonObject(),default).GetAwaiter().GetResult();throw new Exception("Expected error");}
   catch(HttpRequestException ex){Check(!ex.Message.Contains("sensitive")&&!ex.Message.Contains("secret"));}
  });
  test("Incomplete Responses output rejected",()=>{
   var handler=new TestHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("""{"status":"incomplete","output":[]}""")});
   using var http=new HttpClient(handler);var transport=new ModelTransport(http,new(){Provider="openai",AllowCloudDialogue=true,ApiKey="fake"});
   Reject(()=>transport.CompleteAsync("m","https://api.openai.com/v1/responses","p",new(),new JsonObject(),default).GetAwaiter().GetResult());
  });
 }
}
sealed class TestHandler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
{
 public List<JsonObject> Requests=[];public string? Authorization;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
  if(request.Content!=null)Requests.Add(JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))!.AsObject());
  Authorization=request.Headers.Authorization?.ToString();var response=respond(request);response.RequestMessage=request;return response;
 }
}
