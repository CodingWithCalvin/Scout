using System.Net;
using System.Text.Json.Nodes;
namespace Scout.Core;
public sealed class ModelContextException(string message,HttpStatusCode status):HttpRequestException(message,null,status);
public static class ModelRequestError {
 public static HttpRequestException Create(HttpStatusCode status,string body,int inputCharacters,int outputTokens) {
  JsonNode? error=null;
  try{error=(JsonNode.Parse(body) as JsonObject)?["error"] as JsonObject;}catch(System.Text.Json.JsonException){}
  string Field(string name)=>error?[name] is JsonValue v&&v.TryGetValue<string>(out var value)?value:"";
  var code=Field("code").ToLowerInvariant();var type=Field("type").ToLowerInvariant();var message=Field("message").ToLowerInvariant();
  bool context=code is "context_length_exceeded" or "context_size_exceeded"||type is "context_length_exceeded" or "exceed_context_size_error"||message.Contains("exceeds the available context")||message.Contains("context size has been exceeded")||message.Contains("maximum context length")||message.Contains("context window")&&message.Contains("exceed");
  string Counts() {
   var values=new List<string>();
   foreach(var field in new[]{"n_prompt_tokens","n_ctx","n_ctx_slot"})
    if(error?[field] is JsonValue v&&v.TryGetValue<long>(out var n)&&n>=0)values.Add(field+"="+n);
   return values.Count==0?"":" Server counts: "+string.Join(", ",values)+".";
  }
  var details=$" Request text: {inputCharacters:N0} characters (not a token count); requested output: {outputTokens:N0} tokens."+Counts();
  var prefix=$"Model service returned HTTP {(int)status}. ";
  if(context&&status is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
   return new ModelContextException(prefix+"Request exceeds the model context window. Increase Context tokens in Settings > Local models for Scout-managed models, then retry the affected files. External servers must increase their own context limit."+details,status);
  var reason=status switch {
   HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden=>"Provider rejected authentication or model access. Check the configured key and model permissions.",
   HttpStatusCode.TooManyRequests=>"Provider rate or quota limit reached. Check usage limits before retrying.",
   HttpStatusCode.RequestEntityTooLarge=>"Provider rejected the request size.",
   HttpStatusCode.BadRequest when message.Contains("schema")||message.Contains("grammar")||message.Contains("response_format")=>"Provider rejected the structured-output schema or grammar. Check model-server compatibility; increasing context may not help.",
   HttpStatusCode.BadRequest when message.Contains("max_tokens")||message.Contains("max_output_tokens")=>"Provider rejected the requested output-token limit. Check output limits and server capabilities.",
   HttpStatusCode.BadRequest=>"Provider rejected the request format or parameters; no confirmed context overflow. Check model-server compatibility.",
   _=>"Model service could not complete the request. Check server health and model access."
  };
  // Never persist server text: it can echo credentials, dialogue or arbitrary HTML.
  return new HttpRequestException(prefix+reason+details,null,status);
 }
}
