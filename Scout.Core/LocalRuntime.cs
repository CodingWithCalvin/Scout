using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
namespace Scout.Core;
public sealed class LocalRuntime : IAsyncDisposable
{
 private readonly Process process;
 private LocalRuntime(Process p){process=p;}
 public static async Task<LocalRuntime> StartAsync(AppSettings s,IProgress<string> progress,CancellationToken ct) {
  if(!File.Exists(s.RuntimeExecutable)||!File.Exists(s.ModelFile))throw new FileNotFoundException("Choose an existing llama-server.exe and GGUF model in Settings.");
  var uri=DialogueMatcher.LocalEndpoint(s.LocalEndpoint);
  using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp)) {
   try{socket.Bind(new IPEndPoint(IPAddress.Loopback,uri.Port));}catch(SocketException){throw new IOException("Local port occupied. Use the existing-endpoint mode or choose another port.");}
  }
  var start=new ProcessStartInfo(s.RuntimeExecutable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(s.RuntimeExecutable))!};
  foreach(var arg in new[]{"-m",s.ModelFile,"--host","127.0.0.1","--port",uri.Port.ToString(),"-c",s.ContextSize.ToString(),"-ngl",s.GpuLayers.ToString(),"-np","1","-t",s.CpuThreads.ToString(),"-tb",s.CpuThreads.ToString(),"--alias",s.LocalModel,"--reasoning","off"})start.ArgumentList.Add(arg);
  var p=Process.Start(start)??throw new IOException("Cannot start local runtime.");var runtime=new LocalRuntime(p);
  p.OutputDataReceived+=(_,_)=>{};p.ErrorDataReceived+=(_,_)=>{};p.BeginOutputReadLine();p.BeginErrorReadLine();
  try {
   using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(2)};progress.Report("Loading local model…");
   for(int n=0;n<300;n++) {
    ct.ThrowIfCancellationRequested();if(p.HasExited)throw new IOException("Local runtime exited. Check compatibility, RAM, GPU layers and context size.");
    try{using var response=await http.GetAsync($"http://127.0.0.1:{uri.Port}/health",ct);if(response.IsSuccessStatusCode)return runtime;}
    catch(HttpRequestException){}catch(TaskCanceledException) when(!ct.IsCancellationRequested){}
    await Task.Delay(2000,ct);
   }
   throw new TimeoutException("Local model did not become ready within 10 minutes.");
  }catch{await runtime.DisposeAsync();throw;}
 }
 public async ValueTask DisposeAsync() {
  try{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}finally{process.Dispose();}
 }
}
