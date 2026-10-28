using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Scout.Core;
using Microsoft.Win32;
namespace Scout.Desktop;
public partial class MainWindow
{
 private readonly SettingsStore settingsStore=new(Path.Combine(SettingsStore.DefaultDirectory,"UI-Trial"));
 private AppSettings appSettings=new();
 private void UpdateConnectionSummary() {
  var provider=appSettings.Provider switch {"managed"=>"Local · managed runtime","openai"=>"OpenAI","compatible"=>"Compatible API",_=>"Local endpoint"};
  ConnectionSummary.Text=provider+" · "+(string.IsNullOrWhiteSpace(appSettings.Model)?"model not selected":appSettings.Model);
 }
 private void LoadSettings() {
  try{appSettings=settingsStore.Load();appSettings.Validate();}
  catch(Exception ex){SettingsStatus.Text="Settings could not be loaded: "+ex.Message;appSettings=new();}
  ModelId.Text=appSettings.LocalModel;Endpoint.Text=appSettings.LocalEndpoint;
  OpenAiModelEntry.Text=appSettings.OpenAiModel;CompatibleModelEntry.Text=appSettings.CompatibleModel;CompatibleEndpointEntry.Text=appSettings.CompatibleEndpoint;
  CloudConsent.IsChecked=appSettings.AllowCloudDialogue;ModelFolderEntry.Text=appSettings.ModelDirectory;ModelFileEntry.Text=appSettings.ModelFile;RuntimeEntry.Text=appSettings.RuntimeExecutable;
  ThreadsEntry.Text=appSettings.CpuThreads.ToString();GpuEntry.Text=appSettings.GpuLayers.ToString();ContextEntry.Text=appSettings.ContextSize.ToString();
  OutputTokensEntry.Text=appSettings.MaxOutputTokens.ToString();TimeoutEntry.Text=appSettings.RequestTimeoutSeconds.ToString();RequestsEntry.Text=appSettings.MaxRequestsPerRun.ToString();
  AllowDownloadsEntry.IsChecked=appSettings.AllowDownloads;AutoDownloadEntry.IsChecked=appSettings.AutoDownloadMissing;ExperimentalEntry.IsChecked=appSettings.AllowExperimental;
  DownloadProfileChoice.ItemsSource=ModelDownloads.Profiles;DownloadProfileChoice.SelectedItem=ModelDownloads.Profiles.FirstOrDefault(p=>p.Id==appSettings.DownloadProfile)??ModelDownloads.Profiles[0];
  FfmpegEntry.Text=appSettings.Ffmpeg;FfprobeEntry.Text=appSettings.Ffprobe;
  ProviderChoice.SelectedIndex=appSettings.Provider switch{"managed"=>1,"openai"=>2,"compatible"=>3,_=>0};
  UpdateProviderPanels();RefreshKeyStatus();UpdateConnectionSummary();
 }
 private string SelectedProvider=>ProviderChoice.SelectedIndex switch{1=>"managed",2=>"openai",3=>"compatible",_=>"local"};
 private void UpdateProviderPanels() {
  if(LocalConnectionPanel==null)return;
  LocalConnectionPanel.Visibility=ProviderChoice.SelectedIndex<2?Visibility.Visible:Visibility.Collapsed;
  OpenAiPanel.Visibility=ProviderChoice.SelectedIndex==2?Visibility.Visible:Visibility.Collapsed;
  CompatiblePanel.Visibility=ProviderChoice.SelectedIndex==3?Visibility.Visible:Visibility.Collapsed;
 }
 private void ProviderChanged(object sender,SelectionChangedEventArgs e)=>UpdateProviderPanels();
 private AppSettings ReadSettingsControls() {
  int Number(TextBox box,string label)=>int.TryParse(box.Text,out var n)?n:throw new ArgumentException(label+" must be a whole number.");
  return new() {
   Provider=SelectedProvider,LocalModel=ModelId.Text.Trim(),LocalEndpoint=Endpoint.Text.Trim(),OpenAiModel=OpenAiModelEntry.Text.Trim(),
   CompatibleModel=CompatibleModelEntry.Text.Trim(),CompatibleEndpoint=CompatibleEndpointEntry.Text.Trim(),AllowCloudDialogue=CloudConsent.IsChecked==true,
   ModelDirectory=ModelFolderEntry.Text.Trim(),ModelFile=ModelFileEntry.Text.Trim(),RuntimeExecutable=RuntimeEntry.Text.Trim(),
   AllowDownloads=AllowDownloadsEntry.IsChecked==true,AutoDownloadMissing=AutoDownloadEntry.IsChecked==true,AllowExperimental=ExperimentalEntry.IsChecked==true,
   CpuThreads=Number(ThreadsEntry,"CPU threads"),GpuLayers=Number(GpuEntry,"GPU layers"),ContextSize=Number(ContextEntry,"Context tokens"),
   MaxOutputTokens=Number(OutputTokensEntry,"Output token limit"),RequestTimeoutSeconds=Number(TimeoutEntry,"Request timeout"),MaxRequestsPerRun=Number(RequestsEntry,"Request limit"),
   Ffmpeg=FfmpegEntry.Text.Trim(),Ffprobe=FfprobeEntry.Text.Trim(),DownloadProfile=(DownloadProfileChoice.SelectedItem as ModelProfile)?.Id??"qwen36-35b"
  };
 }
 private AppSettings SaveSettingsCore() {
  var settings=ReadSettingsControls();settings.Validate();
  if(!string.IsNullOrWhiteSpace(CompatibleKeyEntry.Password))ConnectionPolicy.CloudUri(settings.CompatibleEndpoint);
  if(!string.IsNullOrWhiteSpace(OpenAiKeyEntry.Password))settingsStore.SetKey("openai|https://api.openai.com/v1/responses",OpenAiKeyEntry.Password.Trim());
  if(!string.IsNullOrWhiteSpace(CompatibleKeyEntry.Password))settingsStore.SetKey("compatible|"+settings.CompatibleEndpoint.TrimEnd('/'),CompatibleKeyEntry.Password.Trim());
  settingsStore.Save(settings);appSettings=settings;OpenAiKeyEntry.Clear();CompatibleKeyEntry.Clear();RefreshKeyStatus();UpdateConnectionSummary();
  SettingsStatus.Text="Settings saved. Keys are protected for this Windows account.";return settings;
 }
 private void SaveSettings(object sender,RoutedEventArgs e){if(operation==null)try{SaveSettingsCore();}catch(Exception ex){Error(ex);}}
 private void RefreshKeyStatus() {
  try{
   OpenAiKeyStatus.Text=settingsStore.ReadKey("openai|https://api.openai.com/v1/responses") is null?"No key saved.":"A key is saved for OpenAI.";
   CompatibleKeyStatus.Text=settingsStore.ReadKey("compatible|"+CompatibleEndpointEntry.Text.Trim().TrimEnd('/')) is null?"No key saved for this endpoint.":"A key is saved for this endpoint.";
  }catch(Exception){OpenAiKeyStatus.Text=CompatibleKeyStatus.Text="Saved keys cannot be unlocked. Enter replacement credentials.";}
 }
 private void RemoveOpenAiKey(object sender,RoutedEventArgs e) {
  try{settingsStore.SetKey("openai|https://api.openai.com/v1/responses",null);OpenAiKeyEntry.Clear();RefreshKeyStatus();UpdateConnectionSummary();}catch(Exception ex){Error(ex);}
 }
 private void RemoveCompatibleKey(object sender,RoutedEventArgs e) {
  try{settingsStore.SetKey("compatible|"+CompatibleEndpointEntry.Text.Trim().TrimEnd('/'),null);CompatibleKeyEntry.Clear();RefreshKeyStatus();UpdateConnectionSummary();}catch(Exception ex){Error(ex);}
 }
 private void BrowseModelFolder(object sender,RoutedEventArgs e){if(PickFolder("Model storage folder") is string p)ModelFolderEntry.Text=p;}
 private void BrowseModelFile(object sender,RoutedEventArgs e) {
  var dialog=new OpenFileDialog{Title="Choose GGUF model",Filter="GGUF model (*.gguf)|*.gguf"};
  if(dialog.ShowDialog(this)==true){ModelFileEntry.Text=dialog.FileName;if(string.IsNullOrWhiteSpace(ModelId.Text))ModelId.Text=Path.GetFileNameWithoutExtension(dialog.FileName);}
 }
 private void BrowseRuntime(object sender,RoutedEventArgs e) {
  var dialog=new OpenFileDialog{Title="Choose llama-server.exe",Filter="Windows executable (*.exe)|*.exe"};
  if(dialog.ShowDialog(this)==true)RuntimeEntry.Text=dialog.FileName;
 }
 private async void DownloadModel(object sender,RoutedEventArgs e) {
  if(operation!=null)return;
  try {
   var settings=SaveSettingsCore();var profile=(ModelProfile)DownloadProfileChoice.SelectedItem;
   ModelDownloads.CheckPolicy(profile,settings);
   operation=new();SetBusy(true);ShowPage("Activity");
   using var http=new HttpClient{Timeout=TimeSpan.FromHours(12)};
   var progress=new Progress<string>(Log);
   var path=await ModelDownloads.DownloadAsync(profile,settings,http,progress,operation.Token);
   ModelFileEntry.Text=path;if(string.IsNullOrWhiteSpace(ModelId.Text))ModelId.Text=Path.GetFileNameWithoutExtension(path);
   SaveSettingsCore();Log("Model ready. Choose launch mode and a compatible llama-server executable to use it.");ShowPage("Settings");
  }catch(OperationCanceledException){Log("Download cancelled. Partial download removed.");}catch(Exception ex){Error(ex);}
  finally{operation?.Dispose();operation=null;SetBusy(false);}
 }
}
