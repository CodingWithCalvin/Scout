using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

static class ModelOutputQualityTests
{
    static readonly Episode[] Episodes = [new(1, 1, 1, "A Rehearsal", "Students rehearse and perform.")];
    static readonly Cue[] Cues = Enumerable.Range(0, 20).Select(i => new Cue(i * 5, "Students rehearse and perform " + i)).ToArray();
    static JsonObject Valid() => JsonNode.Parse("""
    {"assessment":"supported","coverage":"complete","summary":"Students rehearse and perform a play.","candidate_checks":[{"episode_title":"A Rehearsal","supporting_blocks":[0,1],"contradicting_blocks":[],"event_comparison":"The rehearsal leads to a performance.","guide_event":"Students rehearse and perform."}],"matches":[{"episode_title":"A Rehearsal","reason":"Both rehearsal and performance occur."}],"alternatives":[]}
    """)!.AsObject();
    static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    static InvalidDataException Rejected(Action action)
    {
        try { action(); } catch (InvalidDataException ex) { return ex; }
        throw new Exception("Corrupt output was accepted");
    }
    public static void Run(Action<string, Action> test)
    {
        test("Corrupt prose is rejected in every generated explanation field", () =>
        {
            foreach (var field in new[] {"summary", "comparison", "reason", "alternative"})
            foreach (var corrupt in new[] {"//C" + new string('_', 80), string.Concat(Enumerable.Repeat("la ", 15)), string.Concat(Enumerable.Repeat("story continues again. ", 5)), string.Concat(Enumerable.Repeat("із ", 15))})
            {
                var r = Valid();
                if (field == "summary") r["summary"] = corrupt;
                if (field == "comparison") r["candidate_checks"]![0]!["event_comparison"] = corrupt;
                if (field == "reason") r["matches"]![0]!["reason"] = corrupt;
                if (field == "alternative") r["alternatives"] = new JsonArray(new JsonObject {["episode_title"]="Other",["reason_against"]=corrupt});
                var before = r.ToJsonString();
                var error = Rejected(() => DialogueMatcher.Validate(r, DialogueMatcher.Blocks(Cues), Episodes, true));
                Check(error.Message.Contains("Repetitive generated explanation") && before == r.ToJsonString());
            }
        });
        test("Explanation types emptiness and length are validated without grammar help", () =>
        {
            foreach (var invalid in new JsonNode?[] {null, JsonValue.Create(123), JsonValue.Create("  "), JsonValue.Create(new string('a', 651))})
            {
                var r = Valid(); r["summary"] = invalid;
                Rejected(() => DialogueMatcher.Validate(r, DialogueMatcher.Blocks(Cues), Episodes, true));
            }
        });
        test("Quality guard accepts normal multilingual prose and leaves repeated source dialogue alone", () =>
        {
            var r = Valid(); r["summary"] = "Les élèves répètent une pièce. 学生たちは劇を練習します。";
            r["matches"]![0]!["reason"] = "The play is rehearsed; the play is performed; the audience responds.";
            var blocks = DialogueMatcher.Blocks(Cues.Select(c => c with {Text=string.Concat(Enumerable.Repeat("la ", 20))}).ToArray());
            DialogueMatcher.Validate(r, blocks, Episodes, true);
            Check(r["matches"]![0]!.Array("evidence")[0].Text("source_text").Contains("la la"));
            ModelOutputQuality.ValidateText(JsonValue.Create(string.Concat(Enumerable.Repeat("😀", 20))), "unicode", 20);
        });
        test("Malformed citations and corrupt abstentions are held without relying on constrained decoding", () =>
        {
            foreach(var invalid in new JsonNode?[] {null, JsonValue.Create("0"), new JsonObject(), JsonValue.Create(0.5)}) {
                var r=Valid();r["candidate_checks"]![0]!["supporting_blocks"]=new JsonArray(invalid,JsonValue.Create(1));
                Rejected(()=>DialogueMatcher.Validate(r,DialogueMatcher.Blocks(Cues),Episodes,true));
            }
            var abstention=Valid();abstention["assessment"]="non_episode";abstention["coverage"]="unknown";abstention["matches"]=new JsonArray();
            abstention["summary"]=string.Concat(Enumerable.Repeat("own ",15));
            Rejected(()=>DialogueMatcher.Validate(abstention,DialogueMatcher.Blocks(Cues),Episodes,true));
        });
        test("Automatic retry uses original evidence and preserves rejected response", () =>
        {
            var bad = Valid(); bad["summary"] = string.Concat(Enumerable.Repeat("la ", 15));
            var handler = new QualityHandler([bad, Valid(), Valid()]); using var http = new HttpClient(handler);
            var result = new DialogueMatcher(http).IdentifyAsync(Cues, Episodes, "test", "http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(handler.Requests.Count == 3);
            Check(VerificationTests.Evidence(handler.Requests[0]) == VerificationTests.Evidence(handler.Requests[1]));
            Check(!handler.Requests[1]["messages"]![0].Text("content").Contains("la la"));
            Check(result["verification"]!["initial_result"]!.Array("validation_retries").Count == 1);
        });
        test("Repeated validation failure cannot reach verification or create an assignment", () =>
        {
            var bad = Valid(); bad["summary"] = "//C" + new string('_', 80);
            var handler = new QualityHandler([bad, bad.DeepClone().AsObject()]); using var http = new HttpClient(handler);
            var error = Rejected(() => new DialogueMatcher(http).IdentifyAsync(Cues, Episodes, "test", "http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult());
            Check(handler.Requests.Count == 2 && JsonNode.Parse((string)error.Data["model_attempts"]!)!.AsArray().Count == 2);
        });
    }
}
sealed class QualityHandler(List<JsonObject> results) : HttpMessageHandler
{
    public List<JsonObject> Requests = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
        var result = VerificationTests.WireResponse(Requests[^1],results[Requests.Count - 1]);
        return new(HttpStatusCode.OK) {Content = new StringContent(new JsonObject {["choices"]=new JsonArray(new JsonObject {["finish_reason"]="stop",["message"]=new JsonObject {["content"]=result.ToJsonString()}})}.ToJsonString())};
    }
}
