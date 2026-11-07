using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
var input=JsonNode.Parse(File.ReadAllText(args[0]))!;
using var http=new HttpClient(new Capture(args[1]));
var matcher=new DialogueMatcher(http,new(){Provider="local",EnableThinking=true,MaxOutputTokens=8192});
var job=input.Array("jobs").Single(j=>j.Text("id")=="video-11");
try{await matcher.IdentifyAsync(job!["cues"]!.Deserialize<List<Cue>>(Data.Options)!,input["catalog"]!.Deserialize<List<Episode>>(Data.Options)!,"gemma31-thinking","http://127.0.0.1:18779/v1/chat/completions",default);}catch(CapturedException){Console.WriteLine("Request captured without inference.");}
class CapturedException:Exception;
class Capture(string output):HttpMessageHandler {
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){File.WriteAllText(output,await request.Content!.ReadAsStringAsync(ct));throw new CapturedException();}
}
