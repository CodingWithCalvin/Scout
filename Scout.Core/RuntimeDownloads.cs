using System.IO.Compression;
using System.Security.Cryptography;
namespace Scout.Core;

/// <summary>Pinned runtime pairing for Scout's supported Qwen3.6 profile.</summary>
public static class RuntimeDownloads
{
 public const string Version="b11908";
 public static (string Status,string InstalledPath) Installation(AppSettings settings) {
  var profile=Profile(settings);
  var root=Path.Combine(settings.ModelDirectory,"runtimes");
  var executable=Path.Combine(root,profile.Id,"llama-server.exe");
  var archive=Path.Combine(root,profile.FileName);
  if(File.Exists(executable))return (File.Exists(archive)?$"Installed · {profile.Name} · verified before each use":$"Incomplete · {profile.Name} · download / verify to restore archive",executable);
  return ($"Not installed · {profile.Name}","");
 }

 public static ModelProfile Profile(AppSettings settings) {
  if(settings.DownloadProfile!="qwen36-35b")throw new InvalidOperationException("No managed runtime is registered for this model profile.");
  var gpu=settings.GpuLayers>0;var backend=gpu?"vulkan":"cpu";
  var name=$"llama-{Version}-bin-win-{backend}-x64.zip";
  return new($"{Version}-{backend}",$"llama.cpp {Version} ({backend})",name,
   gpu?37141630:24218739,
   gpu?"22f3aa96bb5479be470efb03b370fa203b638bfecec19dacdca402fdd3c7381c":"4f16d1da0b6d58d258002f10f25b79d484b776078f172a569453b73a068f0ed5",
   $"https://github.com/ggml-org/llama.cpp/releases/download/{Version}/{name}",true,"Pinned Windows x64 runtime");
 }
 public static async Task<string> EnsureAsync(AppSettings settings,HttpClient http,IProgress<string> progress,CancellationToken ct) {
  if(!OperatingSystem.IsWindows()||System.Runtime.InteropServices.RuntimeInformation.OSArchitecture!=System.Runtime.InteropServices.Architecture.X64)
   throw new PlatformNotSupportedException("The managed runtime requires Windows x64.");
  var profile=Profile(settings);var root=Path.Combine(settings.ModelDirectory,"runtimes");
  var archivePath=Path.Combine(root,profile.FileName);
  // An installed runtime can be verified and reused offline, even if downloads are now disabled.
  if(!File.Exists(archivePath)&&!settings.AllowDownloads)throw new InvalidOperationException("Enable model and runtime downloads in Settings to install llama-server automatically.");
  var downloadSettings=new AppSettings{ModelDirectory=root,AllowDownloads=true};
  var archive=await ModelDownloads.DownloadAsync(profile,downloadSettings,http,progress,ct);
  return await InstallAsync(archive,Path.Combine(root,profile.Id),progress,ct);
 }
 public static async Task<string> InstallAsync(string archivePath,string target,IProgress<string> progress,CancellationToken ct) {
  using var zip=ZipFile.OpenRead(archivePath);
  var entries=zip.Entries.Where(e=>!string.IsNullOrEmpty(e.Name)).ToArray();
  if(entries.Length==0||entries.Length>500||entries.Sum(e=>e.Length)>1024L*1024*1024)throw new InvalidDataException("Unexpected runtime archive size.");
  var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var entry in zip.Entries) {
   var parts=entry.FullName.Replace('\\','/').Split('/');
   if(Path.IsPathRooted(entry.FullName)||parts.Any(p=>p is ".." or "."||p.Contains(':'))||((entry.ExternalAttributes>>16)&0xF000)==0xA000)
    throw new InvalidDataException("Unsafe runtime archive path.");
  }
  foreach(var entry in entries)if(!names.Add(entry.FullName.Replace('\\','/')))throw new InvalidDataException("Duplicate runtime archive path.");
  if(!names.Contains("llama-server.exe"))throw new InvalidDataException("Runtime archive has no llama-server.exe.");
  if(Directory.Exists(target)) {
   progress.Report("Verifying installed llama-server and libraries…");
   foreach(var entry in entries) {
    ct.ThrowIfCancellationRequested();var path=Path.Combine(target,entry.FullName);
    if(!File.Exists(path))throw new IOException("Managed runtime is incomplete. Remove its version folder and retry.");
    await using var expected=entry.Open();await using var actual=File.OpenRead(path);
    var expectedHash=await SHA256.HashDataAsync(expected,ct);var actualHash=await SHA256.HashDataAsync(actual,ct);
    if(actual.Length!=entry.Length||!CryptographicOperations.FixedTimeEquals(expectedHash,actualHash))
     throw new IOException("Managed runtime verification failed. Remove its version folder and retry.");
   }
   return Path.Combine(target,"llama-server.exe");
  }
  var staging=target+"."+Guid.NewGuid().ToString("N")+".partial";
  try {
   Directory.CreateDirectory(staging);progress.Report("Installing verified llama-server and libraries…");
   foreach(var entry in entries) {
    ct.ThrowIfCancellationRequested();var path=Path.Combine(staging,entry.FullName);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await using var input=entry.Open();await using var output=new FileStream(path,FileMode.CreateNew);await input.CopyToAsync(output,ct);
   }
   ct.ThrowIfCancellationRequested();Directory.Move(staging,target);
   return Path.Combine(target,"llama-server.exe");
  }finally{if(Directory.Exists(staging))Directory.Delete(staging,true);}
 }
}
