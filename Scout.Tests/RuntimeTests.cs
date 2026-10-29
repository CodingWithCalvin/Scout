using Scout.Core;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
static class RuntimeTests {
 public static void Run(Action<string,Action> test,string temp) {
  var progress=new Progress<string>();
  void Assert(bool condition){if(!condition)throw new Exception("Runtime assertion failed.");}
  void Throws(Action action){try{action();}catch(Exception){return;}throw new Exception("Expected failure.");}
  string Archive(string name,string entry) {
   var path=Path.Combine(temp,name+".zip");using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
   using var writer=new StreamWriter(zip.CreateEntry(entry).Open());writer.Write("fixture");return path;
  }
  test("Runtime selects pinned CPU or Vulkan and rejects unknown profiles",()=>{
   Assert(RuntimeDownloads.Profile(new()).Id=="b11908-cpu");Assert(RuntimeDownloads.Profile(new(){GpuLayers=1}).Id=="b11908-vulkan");
   Throws(()=>RuntimeDownloads.Profile(new(){DownloadProfile="unknown"}));
  });
  test("Runtime install reuses verified files and rejects tampering",()=>{
   var archive=Archive("valid-runtime","llama-server.exe");var target=Path.Combine(temp,"runtime-installed");
   var exe=RuntimeDownloads.InstallAsync(archive,target,progress,default).GetAwaiter().GetResult();
   Assert(File.Exists(exe));Assert(RuntimeDownloads.InstallAsync(archive,target,progress,default).GetAwaiter().GetResult()==exe);
   File.WriteAllText(exe,"tampered");Throws(()=>RuntimeDownloads.InstallAsync(archive,target,progress,default).GetAwaiter().GetResult());
  });
  test("Runtime rejects traversal and missing executable",()=>{
   foreach(var entry in new[]{"../outside.exe","C:/outside.exe","folder/file:stream","library.dll"}) {
    var zip=Archive(Guid.NewGuid().ToString(),entry);
    Throws(()=>RuntimeDownloads.InstallAsync(zip,Path.Combine(temp,Guid.NewGuid().ToString()),progress,default).GetAwaiter().GetResult());
   }
   Assert(!File.Exists(Path.Combine(temp,"outside.exe")));
  });
  test("Runtime cancellation leaves no install or staging files",()=>{
   var archive=Archive("cancel-runtime","llama-server.exe");var target=Path.Combine(temp,"cancel-install");
   Throws(()=>RuntimeDownloads.InstallAsync(archive,target,progress,new CancellationToken(true)).GetAwaiter().GetResult());
   Assert(!Directory.Exists(target)&&Directory.GetDirectories(temp,"cancel-install.*").Length==0);
  });
  test("Runtime respects disabled downloads",()=>{
   using var http=new HttpClient(new NoNetwork());var settings=new AppSettings{ModelDirectory=Path.Combine(temp,"disabled-runtime")};
   Throws(()=>RuntimeDownloads.EnsureAsync(settings,http,progress,default).GetAwaiter().GetResult());

  });
  if(Environment.GetEnvironmentVariable("SCOUT_RUNTIME_SMOKE_ARCHIVE") is string source) test("Official runtime archive installs, reuses offline and launches",()=>{
   var settings=new AppSettings{ModelDirectory=Path.Combine(temp,"official-runtime")};var profile=RuntimeDownloads.Profile(settings);
   using(var f=File.OpenRead(source))Assert(f.Length==profile.Bytes&&Convert.ToHexStringLower(SHA256.HashData(f))==profile.Sha256);
   Directory.CreateDirectory(Path.Combine(settings.ModelDirectory,"runtimes"));File.Copy(source,Path.Combine(settings.ModelDirectory,"runtimes",profile.FileName));
   using var http=new HttpClient(new NoNetwork());var exe=RuntimeDownloads.EnsureAsync(settings,http,progress,default).GetAwaiter().GetResult();
   Assert(RuntimeDownloads.EnsureAsync(settings,http,progress,default).GetAwaiter().GetResult()==exe);
   using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe,"--version"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;
   var output=process.StandardOutput.ReadToEnd()+process.StandardError.ReadToEnd();process.WaitForExit();Assert(process.ExitCode==0&&output.Contains("11908"));
  });
 }
 sealed class NoNetwork:HttpMessageHandler {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>throw new Exception("Unexpected network request.");
 }
}
