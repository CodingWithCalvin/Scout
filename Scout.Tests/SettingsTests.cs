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
  test("Local request never sends API credential",()=>{
   var handler=new TestHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"{}"}}]}""")});
   using var http=new HttpClient(handler);var transport=new ModelTransport(http,new(){Provider="local",ApiKey="not-for-local"});
   transport.CompleteAsync("local","http://127.0.0.1:1234/v1/chat/completions","p",new(),new JsonObject(),default).GetAwaiter().GetResult();
   Check(handler.Authorization==null&&handler.Requests[0].ContainsKey("chat_template_kwargs"));
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
