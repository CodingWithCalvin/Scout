using System.Net;
using System.Text.Json.Nodes;
using Scout.Core;

static class VerificationTests
{
    // Existing integration fixtures use the persisted review format. Encode them as
    // the new wire format when the actual request asks for compact verification.
    public static JsonObject WireResponse(JsonNode request,JsonObject legacy) {
        if(request["response_format"]?["json_schema"]?["schema"]?["properties"]?["checks"]==null)return legacy;
        if(legacy["checks"]!=null)return legacy;
        var source=JsonNode.Parse(request["messages"]![1].Text("content"))!;
        var checks=new JsonArray();
        foreach(var c in legacy.Array("candidate_checks")) {
            var episode=source.Array("catalog").Single(e=>e.Text("title")==c.Text("episode_title"));
            bool selected=legacy.Array("matches").Any(m=>m.Text("episode_title")==c.Text("episode_title"));
            checks.Add(new JsonObject{["episode_id"]=episode.Int("id"),["verdict"]=selected?"supported":c.Array("contradicting_blocks").Count>0?"contradicted":"insufficient_evidence",
                ["supporting_blocks"]=c!["supporting_blocks"]!.DeepClone(),["contradicting_blocks"]=c["contradicting_blocks"]!.DeepClone(),["reason"]=c["event_comparison"]!.DeepClone()});
        }
        return new(){["assessment"]=legacy["assessment"]!.DeepClone(),["coverage"]=legacy["coverage"]!.DeepClone(),["checks"]=checks,["note"]=checks.Count==0?legacy.Text("summary"):""};
    }
    public static string Evidence(JsonNode request) {
        var source=JsonNode.Parse(request["messages"]![1].Text("content"))!.AsObject();source.Remove("task");return source.ToJsonString();
    }
    static void Check(bool value){if(!value)throw new Exception("Assertion failed.");}
    static void Reject(Action action){try{action();}catch(InvalidDataException){return;}throw new Exception("Invalid decision accepted.");}
    public static void Run(Action<string,Action> test) {
        test("Prompt JSON preserves literal dialogue without losing JSON escaping or source content",()=>{
            var source=new JsonObject{["dialogue"]=">> I'M Tori & Jade <dialogue> – café \"hello\"\nC:\\folder",["nested"]=new JsonArray("one",2)};
            foreach(var provider in new[]{"managed","compatible","openai"}) {
                var handler=new Handler([new JsonObject()],provider);using var http=new HttpClient(handler);
                var endpoint=provider=="managed"?"http://127.0.0.1:1234/v1/chat/completions":provider=="openai"?"https://api.openai.com/v1/responses":"https://example.com/v1/chat/completions";
                new ModelTransport(http,new(){Provider=provider,AllowCloudDialogue=true,ApiKey="fake"}).CompleteAsync("test",endpoint,"rules",source,new JsonObject(),default).GetAwaiter().GetResult();
                var text=handler.Requests[0][provider=="openai"?"input":"messages"]![1].Text("content");
                Check(text.Contains(">> I'M Tori & Jade")&&text.Contains("café")&&!text.Contains("\\u003E",StringComparison.OrdinalIgnoreCase));
                Check(JsonNode.DeepEquals(JsonNode.Parse(text),source));
            }
        });
        test("Managed CPU Gemma retains its full attention cache without changing other profiles",()=>{
            var s=new AppSettings{Provider="managed",DownloadProfile="gemma4-31b",GpuLayers=0,ThinkingEnabled=false};Check(s.ManagedFullSwaCache&&!s.ManagedThinking);
            s.GpuLayers=8;Check(!s.ManagedFullSwaCache);s.GpuLayers=0;s.Provider="local";Check(!s.ManagedFullSwaCache);s.Provider="managed";
            s.DownloadProfile="qwen36-35b";Check(!s.ManagedFullSwaCache);s.ModelFile=Path.Combine("models","gemma-4-31B-it-Q4_K_M.gguf");Check(s.ManagedFullSwaCache);
            s.ModelFile=Path.Combine("models","custom.gguf");Check(!s.ManagedFullSwaCache);
        });
        var cues=Enumerable.Range(0,32).Select(i=>new Cue(i*5,"Original evidence "+i)).ToArray();var blocks=DialogueMatcher.Blocks(cues);
        Episode[] episodes=[new(1,1,1,"First","First guide."),new(2,1,2,"Second","Second guide.")];
        JsonObject Decision()=>JsonNode.Parse("""{"assessment":"supported","coverage":"complete","checks":[{"episode_id":1,"verdict":"supported","supporting_blocks":[0,1],"contradicting_blocks":[],"reason":"Distinctive events agree."}],"note":""}""")!.AsObject();
        test("Compact verification validates IDs evidence contradictions and abstention",()=>{
            var valid=CompactVerification.Expand(Decision(),blocks,episodes);DialogueMatcher.Validate(valid,blocks,episodes,true);
            Check(valid.Array("matches")[0].Int("episode_id")==1&&valid.Array("candidate_checks")[0].Text("guide_event")=="First guide.");
            foreach(Action<JsonObject> change in new Action<JsonObject>[] {
                d=>d["checks"]![0]!["episode_id"]=999,
                d=>d["checks"]![0]!["supporting_blocks"]=new JsonArray(0),
                d=>d["checks"]![0]!["supporting_blocks"]=new JsonArray(0,999),
                d=>d["checks"]![0]!["contradicting_blocks"]=new JsonArray(2),
                d=>d["checks"]![0]!["supporting_blocks"]=new JsonArray(0,0),
                d=>d["assessment"]="uncertain",
                d=>d["checks"]![0]!["reason"]=string.Concat(Enumerable.Repeat("loop ",20)),
                d=>d["checks"]!.AsArray().Add(d["checks"]![0]!.DeepClone())
            }) {var d=Decision();change(d);Reject(()=>CompactVerification.Expand(d,blocks,episodes));}
            var abstain=Decision();abstain["assessment"]="uncertain";abstain["coverage"]="unknown";abstain["checks"]![0]!["verdict"]="insufficient_evidence";
            Check(CompactVerification.Expand(abstain,blocks,episodes).Array("matches").Count==0);
            abstain["checks"]![0]!["verdict"]="contradicted";Reject(()=>CompactVerification.Expand(abstain,blocks,episodes));
            abstain["checks"]=new JsonArray();Reject(()=>CompactVerification.Expand(abstain,blocks,episodes));abstain["note"]="An interview, with no supported episode framing.";
            abstain["assessment"]="non_episode";Check(CompactVerification.Expand(abstain,blocks,episodes).Text("assessment")=="non_episode");
        });
        test("Compact combined decisions require consecutive distinct ordered evidence",()=>{
            var d=Decision();d["coverage"]="combined";var second=d["checks"]![0]!.DeepClone();second["episode_id"]=2;second["supporting_blocks"]=new JsonArray(2,3);d["checks"]!.AsArray().Add(second);
            Check(CompactVerification.Expand(d,blocks,episodes).Array("matches").Count==2);
            second["supporting_blocks"]=new JsonArray(0,1);Reject(()=>CompactVerification.Expand(d,blocks,episodes));
        });
        test("Repeated candidate checks trigger a fresh model decision without merging the rejected output",()=>{
            var repeated=Decision();var subplot=repeated["checks"]![0]!.DeepClone();subplot["reason"]="A separate subplot also agrees.";
            repeated["checks"]!.AsArray().Add(subplot);
            var handler=new Handler([repeated,Decision()],"managed");using var http=new HttpClient(handler);
            var result=new DialogueMatcher(http).DirectAsync(cues,episodes,"m","http://127.0.0.1:1234/v1/chat/completions",true).GetAwaiter().GetResult();
            Check(handler.Requests.Count==2&&result.Array("matches").Count==1&&result.Array("validation_retries").Count==1);
            Check(result.Array("validation_retries")[0]!["response"].Array("checks").Count==2);
            var retry=JsonNode.Parse(handler.Requests[1]["messages"]![1].Text("content"))!;
            Check(retry.Text("task").Contains("ONE check per episode ID")&&Evidence(handler.Requests[0])==Evidence(handler.Requests[1]));
            Check(handler.Requests[0]["response_format"]!["json_schema"]!["schema"]!["properties"]!["checks"].Int("maxItems")==episodes.Length);
        });
        test("Both passes share rules and full dialogue prefix without leaking generated decisions",()=>{
            foreach(var provider in new[]{"managed","compatible","openai"}) {
                var initial=JsonNode.Parse("""{"assessment":"supported","coverage":"complete","summary":"INITIAL GENERATED SUMMARY","candidate_checks":[{"episode_title":"First","supporting_blocks":[0,1],"contradicting_blocks":[],"event_comparison":"Distinctive events agree."}],"matches":[{"episode_title":"First","reason":"First distinctive events."}],"alternatives":[]}""")!.AsObject();
                var handler=new Handler([initial,Decision()],provider);using var http=new HttpClient(handler);
                var matcher=new DialogueMatcher(http,new(){Provider=provider,AllowCloudDialogue=true,ApiKey="fake",EnableThinking=false});
                var endpoint=provider=="managed"?"http://127.0.0.1:1234/v1/chat/completions":provider=="openai"?"https://api.openai.com/v1/responses":"https://example.com/v1/chat/completions";
                var result=matcher.IdentifyAsync(cues,episodes,"test",endpoint).GetAwaiter().GetResult();
                var field=provider=="openai"?"input":"messages";var first=handler.Requests[0];var second=handler.Requests[1];
                Check(first[field]![0].Text("content")==second[field]![0].Text("content"));
                var a=first[field]![1].Text("content");var b=second[field]![1].Text("content");
                int prefix=a.IndexOf(",\"catalog\":",StringComparison.Ordinal);Check(prefix>500&&a[..prefix]==b[..prefix]);
                Check(!b.Contains("INITIAL GENERATED SUMMARY")&&result.Text("summary")=="INITIAL GENERATED SUMMARY");
                Check(provider=="managed"?first.Bool("cache_prompt")&&second.Bool("cache_prompt"):!first.ContainsKey("cache_prompt"));
                Check(result.Text("summary_source")=="initial_pass"&&result["verification_decision"]!=null);
            }
        });
        test("A changed verifier assignment never inherits the original summary",()=>{
            var initial=JsonNode.Parse("""{"assessment":"supported","coverage":"complete","summary":"WRONG INITIAL SUMMARY","candidate_checks":[{"episode_title":"First","supporting_blocks":[0,1],"contradicting_blocks":[],"event_comparison":"Distinctive events agree."}],"matches":[{"episode_title":"First","reason":"First events."}],"alternatives":[{"episode_title":"Second","reason_against":"Other events."}]}""")!.AsObject();
            var changed=Decision();changed["checks"]![0]!["episode_id"]=2;
            using var http=new HttpClient(new Handler([initial,changed],"managed"));var result=new DialogueMatcher(http).IdentifyAsync(cues,episodes,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(result.Array("matches")[0].Int("episode_id")==2&&!result.Text("summary").Contains("WRONG INITIAL"));
        });
    }
    sealed class Handler(List<JsonObject> replies,string provider):HttpMessageHandler {
        public List<JsonObject> Requests=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());var content=replies[Requests.Count-1].ToJsonString();
            var response=provider=="openai"?new JsonObject{["status"]="completed",["output"]=new JsonArray(new JsonObject{["type"]="message",["content"]=new JsonArray(new JsonObject{["type"]="output_text",["text"]=content})})}:
                new JsonObject{["choices"]=new JsonArray(new JsonObject{["finish_reason"]="stop",["message"]=new JsonObject{["content"]=content}})};
            return new(HttpStatusCode.OK){Content=new StringContent(response.ToJsonString())};
        }
    }
}
