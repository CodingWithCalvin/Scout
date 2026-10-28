using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Scout.Core;
public sealed class InferenceOptions
{
 public string Provider {get;init;}="local";
 public bool AllowCloudDialogue {get;init;}
 [System.Text.Json.Serialization.JsonIgnore] public string? ApiKey {get;init;}
 public int MaxOutputTokens {get;init;}=2800;
 public int MaxRequests {get;init;}=200;
}
public sealed class UsageLimitException():InvalidOperationException("Per-run request limit reached. Completed results are saved. Increase the limit in Settings to process more files.");
public sealed record UsageSnapshot(int Requests,long InputTokens,long OutputTokens,string Provider);
public sealed class ModelTransport(HttpClient client,InferenceOptions options)
{
 private int requests;private long inputTokens,outputTokens;
 public string CacheScope=>$"{options.Provider}|{options.MaxOutputTokens}|responses-v1";
 public UsageSnapshot Usage=>new(requests,inputTokens,outputTokens,options.Provider);
 public Uri ValidateEndpoint(string endpoint)=>ConnectionPolicy.Validate(options.Provider,endpoint,options.AllowCloudDialogue,options.ApiKey);
 public async Task<JsonObject> CompleteAsync(string model,string endpoint,string system,JsonObject source,JsonNode schema,CancellationToken ct) {
  var uri=ValidateEndpoint(endpoint);
  if(options.MaxOutputTokens<512||options.MaxOutputTokens>65536||options.MaxRequests<2)throw new ArgumentException("Invalid inference limits.");
  if(requests>=options.MaxRequests)throw new UsageLimitException();
  bool responses=options.Provider=="openai",local=options.Provider is "local" or "managed";
  JsonObject payload;
  if(responses) {
   payload=new JsonObject{["model"]=model,["store"]=false,["max_output_tokens"]=options.MaxOutputTokens,
    ["input"]=JsonSerializer.SerializeToNode(new[]{new{role="system",content=system},new{role="user",content=source.ToJsonString()}}),
    ["text"]=new JsonObject{["format"]=new JsonObject{["type"]="json_schema",["name"]="episode_match",["strict"]=true,["schema"]=schema.DeepClone()}}};
  }else{
   payload=new JsonObject{["model"]=model,["stream"]=false,
    ["messages"]=JsonSerializer.SerializeToNode(new[]{new{role="system",content=system},new{role="user",content=source.ToJsonString()}}),
    ["response_format"]=new JsonObject{["type"]="json_schema",["json_schema"]=new JsonObject{["name"]="episode_match",["strict"]=true,["schema"]=schema.DeepClone()}}};
   payload["max_tokens"]=options.MaxOutputTokens;
   if(local){payload["temperature"]=0;payload["seed"]=42;payload["chat_template_kwargs"]=new JsonObject{["enable_thinking"]=false};}
  }
  using var request=new HttpRequestMessage(HttpMethod.Post,uri){Content=JsonContent.Create(payload)};
  if(!local)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",options.ApiKey);
  requests++;
  using var response=await client.SendAsync(request,ct);
  if(!response.IsSuccessStatusCode)throw new HttpRequestException($"Model service returned HTTP {(int)response.StatusCode}. Check provider, model access, key and limits.",null,response.StatusCode);
  var body=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))??throw new InvalidDataException("Empty model response.");
  var usage=body["usage"];
  inputTokens+=usage?[responses?"input_tokens":"prompt_tokens"]?.GetValue<long>()??0;
  outputTokens+=usage?[responses?"output_tokens":"completion_tokens"]?.GetValue<long>()??0;
  string content;
  if(responses) {
   if(body.Text("status")!="completed")throw new InvalidDataException("Model response incomplete; no assignment accepted.");
   var parts=body.Array("output").Where(n=>n.Text("type")=="message").SelectMany(n=>n.Array("content")).ToList();
   if(parts.Any(n=>n.Text("type")=="refusal"))throw new InvalidDataException("Model declined this request; no assignment accepted.");
   content=string.Concat(parts.Where(n=>n.Text("type")=="output_text").Select(n=>n.Text("text")));
  }else{
   var choice=body["choices"]?[0]??throw new InvalidDataException("No model choice.");
   if(choice.Text("finish_reason")!="stop")throw new InvalidDataException("Incomplete model response.");
   if(!string.IsNullOrWhiteSpace(choice["message"].Text("refusal")))throw new InvalidDataException("Model declined this request.");
   content=choice["message"].Text("content");
  }
  return JsonNode.Parse(content)?.AsObject()??throw new InvalidDataException("Invalid model JSON.");
 }
}
