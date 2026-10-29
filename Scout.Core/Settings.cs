using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Scout.Core;

public sealed class AppSettings
{
    public NamingOptions Naming {get;set;}=new();
    public string MetadataProvider {get;set;}="tvdb";
    public string Provider {get;set;}="local";
    public string LocalModel {get;set;}="";
    public string LocalEndpoint {get;set;}="http://127.0.0.1:18765/v1/chat/completions";
    public string OpenAiModel {get;set;}="";
    public string CompatibleModel {get;set;}="";
    public string CompatibleEndpoint {get;set;}="";
    public bool AllowCloudDialogue {get;set;}
    public string ModelDirectory {get;set;}=Path.Combine(SettingsStore.DefaultDirectory,"models");
    public string ModelFile {get;set;}="";
    public bool AllowDownloads {get;set;}
    public bool AutoDownloadMissing {get;set;}
    public bool AllowExperimental {get;set;}
    public string DownloadProfile {get;set;}="qwen36-35b";
    public int CpuThreads {get;set;}=8;
    public int GpuLayers {get;set;}=0;
    public int ContextSize {get;set;}=24576;
    public int MaxOutputTokens {get;set;}=4096;
    public int RequestTimeoutSeconds {get;set;}=600;
    public int MaxRequestsPerRun {get;set;}=200;
    public string Ffmpeg {get;set;}="ffmpeg";
    public string Ffprobe {get;set;}="ffprobe";
    [JsonIgnore] public string Endpoint=>Provider switch {"openai"=>"https://api.openai.com/v1/responses","compatible"=>CompatibleEndpoint,_=>LocalEndpoint};
    [JsonIgnore] public string Model=>Provider switch {"openai"=>OpenAiModel,"compatible"=>CompatibleModel,_=>LocalModel};
    [JsonIgnore] public string CredentialId=>Provider+"|"+Endpoint.Trim().TrimEnd('/');
    public void Validate() {
        Naming.Validate();
        if(MetadataProvider is not ("tvdb" or "tmdb" or "tvmaze"))throw new ArgumentException("Unknown metadata provider.");
        if(Provider is not ("local" or "managed" or "openai" or "compatible"))throw new ArgumentException("Unknown model provider.");
        if(CpuThreads<1||CpuThreads>256||GpuLayers<0||GpuLayers>999||ContextSize<4096||ContextSize>1048576)throw new ArgumentException("Check CPU threads, GPU layers and context size.");
        if(MaxOutputTokens<512||MaxOutputTokens>65536||RequestTimeoutSeconds<30||RequestTimeoutSeconds>7200||MaxRequestsPerRun<2||MaxRequestsPerRun>10000)
            throw new ArgumentException("Limits: output 512–65536 tokens, timeout 30–7200 seconds, requests 2–10000.");
        if(string.IsNullOrWhiteSpace(ModelDirectory)||!Path.IsPathFullyQualified(ModelDirectory))throw new ArgumentException("Choose an absolute model storage folder.");
        if(AutoDownloadMissing&&!AllowDownloads)throw new ArgumentException("Automatic downloads require downloads to be allowed.");
        if(Provider is "local" or "managed")DialogueMatcher.LocalEndpoint(LocalEndpoint);
        else if(Provider=="compatible"&&!string.IsNullOrWhiteSpace(CompatibleEndpoint))ConnectionPolicy.CloudUri(CompatibleEndpoint);
    }
}
public static class ConnectionPolicy
{
    public static string ServerProvider(string endpoint) {
        if(Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)&&uri.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1") {DialogueMatcher.LocalEndpoint(endpoint);return "local";}
        CloudUri(endpoint);return "compatible";
    }
    public static Uri CloudUri(string endpoint) {
        if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!string.IsNullOrEmpty(uri.UserInfo)||!string.IsNullOrEmpty(uri.Query)||!string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Cloud endpoints must use HTTPS without embedded credentials, query strings or fragments.");
        return uri;
    }
    public static Uri Validate(string provider,string endpoint,bool consent,string? key) {
        if(provider is "local" or "managed")return DialogueMatcher.LocalEndpoint(endpoint);
        if(provider is not ("openai" or "compatible"))throw new ArgumentException("Unknown provider.");
        if(!consent)throw new InvalidOperationException("Enable cloud dialogue processing in Settings before sending dialogue.");
        var uri=CloudUri(endpoint);
        if(provider=="openai"&&uri.AbsoluteUri!="https://api.openai.com/v1/responses")throw new ArgumentException("OpenAI credentials can only be sent to the OpenAI Responses endpoint.");
        if(string.IsNullOrWhiteSpace(key))throw new ArgumentException("Save an API key for the selected provider and endpoint.");
        return uri;
    }
}
public sealed class SettingsStore(string directory)
{
    // Continue using existing protected preferences/history after the product rename.
    public static string DefaultDirectory {
        get {
            var root=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var legacy=Path.Combine(root,"Episcout");
            return Directory.Exists(legacy)?legacy:Path.Combine(root,"Scout");
        }
    }
    private string SettingsPath=>Path.Combine(directory,"settings.json");
    private string SecretsPath=>Path.Combine(directory,"credentials.dpapi");
    public AppSettings Load()=>File.Exists(SettingsPath)?JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath),Data.Options)??new():new();
    public void Save(AppSettings settings){settings.Validate();Data.Save(SettingsPath,settings);}
    private Dictionary<string,string> Keys()=>File.Exists(SecretsPath)?JsonSerializer.Deserialize<Dictionary<string,string>>(WindowsSecrets.Unprotect(File.ReadAllBytes(SecretsPath)))??[]:[];
    public string? ReadKey(string id)=>Keys().GetValueOrDefault(id);
    public void SetKey(string id,string? key) {
        var keys=Keys();if(string.IsNullOrWhiteSpace(key))keys.Remove(id);else keys[id]=key;
        Directory.CreateDirectory(directory);
        var protectedBytes=WindowsSecrets.Protect(JsonSerializer.SerializeToUtf8Bytes(keys));
        var temp=SecretsPath+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temp,protectedBytes);File.Move(temp,SecretsPath,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
public static class WindowsSecrets
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob {public int Size;public IntPtr Data;}
    [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input,string description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Protect(byte[] bytes)=>Transform(bytes,true);
    public static byte[] Unprotect(byte[] bytes)=>Transform(bytes,false);
    private static byte[] Transform(byte[] bytes,bool protect) {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Windows credential protection required.");
        var input=new Blob {Size=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};Blob output=default;
        try {
            Marshal.Copy(bytes,0,input.Data,bytes.Length);
            bool ok=protect?CryptProtectData(ref input,"Scout credentials",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output):
                CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);
            if(!ok)throw new CryptographicException("Windows could not protect or unlock the saved credentials.",new Win32Exception(Marshal.GetLastWin32Error()));
            byte[] result=new byte[output.Size];Marshal.Copy(output.Data,result,0,output.Size);return result;
        } finally {
            Marshal.Copy(new byte[bytes.Length],0,input.Data,bytes.Length);Marshal.FreeHGlobal(input.Data);
            if(output.Data!=IntPtr.Zero){Marshal.Copy(new byte[output.Size],0,output.Data,output.Size);LocalFree(output.Data);}
        }
    }
}
