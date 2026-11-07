using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using Scout.Core;
namespace Scout.Desktop;
public partial class App {
 private Mutex? instanceMutex;
 private bool ownsInstance;
 private readonly CancellationTokenSource activationStop=new();
 private string ActivationName=>"Scout-"+CollectionResolver.Hash(WindowsIdentity.GetCurrent().User!.Value+":"+Process.GetCurrentProcess().SessionId)[..24];
 private bool ClaimInstance(string[] args) {
  instanceMutex=new Mutex(false,@"Local\"+ActivationName);
  try{ownsInstance=instanceMutex.WaitOne(0);}catch(AbandonedMutexException){ownsInstance=true;}
  if(ownsInstance)return true;
  if(args.Length!=0&&(args.Length!=2||args[0]!="--scout"||args[1].Length!=32||!args[1].All(Uri.IsHexDigit)))
   throw new InvalidOperationException("Open reports from the existing Scout window.");
  using var pipe=new NamedPipeClientStream(".",ActivationName,PipeDirection.Out,PipeOptions.CurrentUserOnly);
  pipe.Connect(5000);
  using var writer=new StreamWriter(pipe);writer.WriteLine(args.Length==2?args[1]:"");writer.Flush();
  Shutdown();return false;
 }
 private void ListenForActivation(MainWindow window) {
  var ct=activationStop.Token;
  _=Task.Run(async()=>{
   while(!ct.IsCancellationRequested) {
    try {
     using var pipe=new NamedPipeServerStream(ActivationName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
     await pipe.WaitForConnectionAsync(ct);
     using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(5));
     using var reader=new StreamReader(pipe);
     // Only a fixed-length Scout ID is accepted, never a file path or shell command.
     var chars=new char[34];int count=0;
     while(count<chars.Length) {var n=await reader.ReadAsync(chars.AsMemory(count,1),timeout.Token);if(n==0||chars[count]=='\n')break;count++;}
     var id=new string(chars,0,count).TrimEnd('\r');
     if(id.Length==0||id.Length==32&&id.All(Uri.IsHexDigit))await Dispatcher.InvokeAsync(()=>window.OpenTaskbarScout(id));
    }catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
    catch(Exception ex) when(ex is IOException or OperationCanceledException){Debug.WriteLine("Scout activation unavailable: "+ex.GetType().Name);}
   }
  },ct);
 }
 protected override void OnExit(ExitEventArgs e) {
  activationStop.Cancel();
  if(ownsInstance)instanceMutex?.ReleaseMutex();
  instanceMutex?.Dispose();base.OnExit(e);
 }
}
