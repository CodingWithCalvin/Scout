using System.Diagnostics;
using System.Security.Cryptography;
namespace Scout.Core;
public sealed record ModelProfile(string Id,string Name,string FileName,long Bytes,string Sha256,string Url,bool Approved,string Status)
{
 public string Display=>$"{Name} · {Bytes/1e9:F1} GB · {(Approved?"approved":"experimental")}";
}
public static class ModelDownloads
{
 public static IReadOnlyList<ModelProfile> Profiles {get;}=[
  new("qwen36-35b","Qwen3.6 35B A3B · UD Q4_K_M","Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",22134528992,
  "ac0e2c1189e055faa36eff361580e79c5bd6f8e76bffb4ce547f167d53e31a61",
  "https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF/resolve/a483e9e6cbd595906af30beda3187c2663a1118c/Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",false,
  "Tested on Victorious; not approved as a general default. Requires substantial RAM. Managed llama.cpp b11908 runtime downloaded automatically.")
 ];
 public static void CheckPolicy(ModelProfile p,AppSettings s) {
  if(!s.AllowDownloads)throw new InvalidOperationException("Model downloads are disabled in Settings.");
  if(!p.Approved&&!s.AllowExperimental)throw new InvalidOperationException("Enable experimental models to download this profile.");
  if(Path.GetFileName(p.FileName)!=p.FileName||p.FileName.Contains('/')||p.FileName.Contains('\\')||p.Bytes<1||p.Sha256.Length!=64||!Uri.TryCreate(p.Url,UriKind.Absolute,out var uri)||uri.Scheme!="https")throw new InvalidDataException("Invalid model manifest.");
 }
 public static async Task<string> DownloadAsync(ModelProfile profile,AppSettings settings,HttpClient http,IProgress<string> progress,CancellationToken ct) {
  CheckPolicy(profile,settings);Directory.CreateDirectory(settings.ModelDirectory);var target=Path.Combine(settings.ModelDirectory,profile.FileName);
  if(File.Exists(target)) {
   progress.Report("Verifying model SHA256…");await using var file=File.OpenRead(target);
   if(file.Length==profile.Bytes&&Convert.ToHexStringLower(await SHA256.HashDataAsync(file,ct))==profile.Sha256)return target;
   throw new IOException("An unverified file exists at the model path. Choose another folder; it will not be overwritten.");
  }
  var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(target))!);
  if(drive.IsReady&&drive.AvailableFreeSpace<profile.Bytes+1024L*1024*1024)throw new IOException("Not enough free space for the model plus 1 GB working space.");
  var part=target+"."+Guid.NewGuid().ToString("N")+".part";
  try {
   using var response=await http.GetAsync(profile.Url,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
   if(response.RequestMessage?.RequestUri?.Scheme!="https")throw new IOException("Insecure download redirect.");
   if(response.Content.Headers.ContentLength is long size&&size!=profile.Bytes)throw new IOException("Download size differs from pinned manifest.");
   await using(var input=await response.Content.ReadAsStreamAsync(ct))
   await using(var output=new FileStream(part,FileMode.CreateNew,FileAccess.Write,FileShare.None,1048576,true)) {
    using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] buffer=new byte[1048576];long total=0;var timer=Stopwatch.StartNew();int read;
    while((read=await input.ReadAsync(buffer,ct))>0) {
     total+=read;if(total>profile.Bytes)throw new IOException("Download exceeds pinned size.");
     await output.WriteAsync(buffer.AsMemory(0,read),ct);hash.AppendData(buffer,0,read);
     if(timer.ElapsedMilliseconds>500){progress.Report($"Downloading {profile.Name}: {total/1e9:F2} / {profile.Bytes/1e9:F2} GB");timer.Restart();}
    }
    if(total!=profile.Bytes||Convert.ToHexStringLower(hash.GetHashAndReset())!=profile.Sha256)throw new IOException("Checksum mismatch. Model not installed.");
   }
   File.Move(part,target);progress.Report("Model verified and ready.");return target;
  }finally{if(File.Exists(part))File.Delete(part);}
 }
}
