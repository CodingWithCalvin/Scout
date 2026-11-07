using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Scout.Core;
sealed record Arm(string Id,int Profile,bool Thinking);
sealed class ProgressCallback<T>(Action<T> callback):IProgress<T>{public void Report(T value)=>callback(value);}
sealed class Runtime(Process process,string endpoint):IAsyncDisposable {
 readonly List<string> lines=[];public string[] Tail{get{lock(lines)return lines.ToArray();}}public string Endpoint=>endpoint;
 public static async Task<Runtime> Start(string exe,string model,Arm arm,AppSettings s,Action<string> log) {
  // A new ephemeral port avoids Windows TIME_WAIT conflicts between per-case servers.
  int port;using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp)) {
   socket.Bind(new IPEndPoint(IPAddress.Loopback,0));port=((IPEndPoint)socket.LocalEndPoint!).Port;
  }
  var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(exe)};
  foreach(var arg in new[]{"-m",model,"--host","127.0.0.1","--port",port.ToString(),"-c",s.ContextSize.ToString(),"-ngl",s.GpuLayers.ToString(),"-np","1","-t",s.CpuThreads.ToString(),"-tb",s.CpuThreads.ToString(),"--alias",arm.Id,"--reasoning",arm.Thinking?"on":"off","--reasoning-budget",arm.Thinking?"2048":"0","--reasoning-format","deepseek"})start.ArgumentList.Add(arg);
  var process=Process.Start(start)??throw new IOException("Runtime did not start.");var runtime=new Runtime(process,$"http://127.0.0.1:{port}/v1/chat/completions");
  void Line(object sender,DataReceivedEventArgs e){if(e.Data!=null)lock(runtime.lines){runtime.lines.Add(e.Data);if(runtime.lines.Count>100)runtime.lines.RemoveAt(0);}}
  process.OutputDataReceived+=Line;process.ErrorDataReceived+=Line;process.BeginOutputReadLine();process.BeginErrorReadLine();
  try {
   using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(3)};
   for(int n=0;n<300;n++) {
    if(process.HasExited)throw new IOException("Runtime exited: "+string.Join("\n",runtime.Tail.TakeLast(15)));
    try{using var response=await http.GetAsync($"http://127.0.0.1:{port}/health");if(response.IsSuccessStatusCode){log("Runtime ready: "+arm.Id);return runtime;}}catch(HttpRequestException){}catch(TaskCanceledException){}
    await Task.Delay(2000);
   }throw new TimeoutException("Runtime load timed out.");
  }catch{await runtime.DisposeAsync();throw;}
 }
 public async ValueTask DisposeAsync(){try{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}finally{process.Dispose();}}
}
