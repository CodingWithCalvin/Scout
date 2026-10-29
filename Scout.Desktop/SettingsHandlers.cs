using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Scout.Core;
using Microsoft.Win32;
namespace Scout.Desktop;
public partial class MainWindow
{
 private readonly SettingsStore settingsStore=new(Environment.GetEnvironmentVariable("SCOUT_SETTINGS")??Path.Combine(SettingsStore.DefaultDirectory,"UI-Trial"));
 private AppSettings appSettings=new();
 private bool settingsDownload;
 private void RefreshManagedRuntime() {
  if(ManagedRuntimeStatus==null||ManagedRuntimePath==null||ManagedRuntimeUsage==null||GpuEntry==null||ModelFolderEntry==null||DownloadProfileChoice==null)return;
  ManagedRuntimeUsage.Text="Scout downloads, verifies and manages llama-server automatically.";
  try {
   if(!int.TryParse(GpuEntry.Text,out var layers)||layers<0||layers>999||!Path.IsPathFullyQualified(ModelFolderEntry.Text.Trim())) {
    ManagedRuntimeStatus.Text="Choose a valid model folder and GPU-layer count to check the runtime.";ManagedRuntimePath.Clear();return;
   }
   var settings=new AppSettings{ModelDirectory=ModelFolderEntry.Text.Trim(),GpuLayers=layers,DownloadProfile=(DownloadProfileChoice.SelectedItem as ModelProfile)?.Id??appSettings.DownloadProfile};
   var installed=RuntimeDownloads.Installation(settings);
   ManagedRuntimeStatus.Text=installed.Status;ManagedRuntimePath.Text=installed.InstalledPath;
  }catch(Exception ex) when(ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException){ManagedRuntimeStatus.Text="Could not check managed runtime: "+ex.Message;ManagedRuntimePath.Clear();}
 }
 private void RuntimeLocationChanged(object sender,TextChangedEventArgs e)=>RefreshManagedRuntime();
 private void RuntimeProfileChanged(object sender,SelectionChangedEventArgs e)=>RefreshManagedRuntime();
 private bool refreshingLocalModels;
 private void RefreshLocalModels() {
  if(ModelId==null||ModelFolderEntry==null||ModelFileEntry==null)return;
  var alias=ModelId.Text;refreshingLocalModels=true;
  try {
   var models=LocalModels.Find(ModelFolderEntry.Text.Trim(),ModelFileEntry.Text.Trim());
   ModelId.ItemsSource=models;
   ModelId.SelectedItem=models.FirstOrDefault(m=>string.Equals(m.Path,ModelFileEntry.Text.Trim(),StringComparison.OrdinalIgnoreCase)&&m.Name==alias);
   ModelId.Text=alias;
   LocalModelsHint.Text=models.Count==0?"No local GGUF models found. Download one under Local models, or enter an existing endpoint’s model ID.":$"{models.Count} local model(s) available. Selecting one sets its GGUF file. Existing endpoints also accept a typed model ID.";
  }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException){LocalModelsHint.Text="Could not list local models: "+ex.Message;}
  finally{refreshingLocalModels=false;}
 }
 private void LocalModelsOpened(object? sender,EventArgs e)=>RefreshLocalModels();
 private void LocalModelSelected(object sender,SelectionChangedEventArgs e) {
  if(refreshingLocalModels||ModelId.SelectedItem is not LocalModelFile model||ModelFileEntry==null)return;
  ModelFileEntry.Text=model.Path;ProviderChoice.SelectedIndex=1;
  var profile=ModelDownloads.Profiles.FirstOrDefault(p=>p.FileName.Equals(Path.GetFileName(model.Path),StringComparison.OrdinalIgnoreCase));
  if(profile!=null)DownloadProfileChoice.SelectedItem=profile;
 }

 private void UpdateConnectionSummary() {
  var provider=appSettings.Provider switch {"managed"=>"Local · managed runtime","openai"=>"OpenAI","compatible"=>"Compatible API",_=>"Local endpoint"};
  ConnectionSummary.Text=provider+" · "+(string.IsNullOrWhiteSpace(appSettings.Model)?"model not selected":appSettings.Model);
 }
 private void LoadSettings() {
  try{appSettings=settingsStore.Load();appSettings.Validate();}
  catch(Exception ex){SettingsStatus.Text="Settings could not be loaded: "+ex.Message;appSettings=new();}
  ShowProvider.SelectedIndex=appSettings.MetadataProvider switch{"tmdb"=>1,"tvmaze"=>2,_=>0};
  ModelId.Text=appSettings.LocalModel;Endpoint.Text=appSettings.LocalEndpoint;
  OpenAiModelEntry.Text=appSettings.OpenAiModel;CompatibleModelEntry.Text=appSettings.Provider=="local"?appSettings.LocalModel:appSettings.CompatibleModel;CompatibleEndpointEntry.Text=appSettings.Provider=="local"?appSettings.LocalEndpoint:appSettings.CompatibleEndpoint;
  CloudConsent.IsChecked=appSettings.AllowCloudDialogue;ModelFolderEntry.Text=appSettings.ModelDirectory;ModelFileEntry.Text=appSettings.ModelFile;
  ThreadsEntry.Text=appSettings.CpuThreads.ToString();GpuEntry.Text=appSettings.GpuLayers.ToString();ContextEntry.Text=appSettings.ContextSize.ToString();
  OutputTokensEntry.Text=appSettings.MaxOutputTokens.ToString();TimeoutEntry.Text=appSettings.RequestTimeoutSeconds.ToString();RequestsEntry.Text=appSettings.MaxRequestsPerRun.ToString();
  AllowDownloadsEntry.IsChecked=appSettings.AllowDownloads;AutoDownloadEntry.IsChecked=appSettings.AutoDownloadMissing;ExperimentalEntry.IsChecked=appSettings.AllowExperimental;
  DownloadProfileChoice.ItemsSource=ModelDownloads.Profiles;DownloadProfileChoice.SelectedItem=ModelDownloads.Profiles.FirstOrDefault(p=>p.Id==appSettings.DownloadProfile)??ModelDownloads.Profiles[0];
  FfmpegEntry.Text=appSettings.Ffmpeg;FfprobeEntry.Text=appSettings.Ffprobe;
  ProviderChoice.SelectedIndex=appSettings.Provider switch{"managed"=>1,"openai"=>2,"compatible"=>0,_=>0};
  UpdateProviderPanels();RefreshKeyStatus();UpdateConnectionSummary();LoadNamingControls();RefreshLocalModels();RefreshManagedRuntime();
 }
 private string SelectedProvider=>ProviderChoice.SelectedIndex switch{1=>"managed",2=>"openai",_=>ConnectionPolicy.ServerProvider(CompatibleEndpointEntry.Text.Trim())};
 private void UpdateProviderPanels() {
  if(LocalConnectionPanel==null)return;
  LocalConnectionPanel.Visibility=ProviderChoice.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed;
  if(LocalModeHelp!=null)LocalModeHelp.Text=ProviderChoice.SelectedIndex==1?"Scout starts llama-server with your selected GGUF, waits for it to load, and stops it when processing finishes.":"Connects to a server you already started. Scout does not launch a model in this mode. For a downloaded GGUF, choose Let Scout Launch my Model.";
  OpenAiPanel.Visibility=ProviderChoice.SelectedIndex==2?Visibility.Visible:Visibility.Collapsed;
  CompatiblePanel.Visibility=ProviderChoice.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;
  UpdateCloudPrivacyVisibility();
 }
 private void ProviderChanged(object sender,SelectionChangedEventArgs e)=>UpdateProviderPanels();
 private void ServerEndpointChanged(object sender,TextChangedEventArgs e)=>UpdateCloudPrivacyVisibility();
 private void UpdateCloudPrivacyVisibility() {
  if(CloudPrivacyPanel==null||ProviderChoice==null||CompatibleEndpointEntry==null)return;
  var remote=ProviderChoice.SelectedIndex==2;
  if(ProviderChoice.SelectedIndex==0&&Uri.TryCreate(CompatibleEndpointEntry.Text.Trim(),UriKind.Absolute,out var endpoint))
   remote=endpoint.Host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1");
  CloudPrivacyPanel.Visibility=remote?Visibility.Visible:Visibility.Collapsed;
 }

 private AppSettings ReadSettingsControls() {
  int Number(TextBox box,string label)=>int.TryParse(box.Text,out var n)?n:throw new ArgumentException(label+" must be a whole number.");
  return new() {
   Naming=DefaultNaming(),MetadataProvider=SelectedMetadataProvider,
   Provider=SelectedProvider,LocalModel=SelectedProvider=="local"?CompatibleModelEntry.Text.Trim():ModelId.Text.Trim(),LocalEndpoint=SelectedProvider=="local"?CompatibleEndpointEntry.Text.Trim():Endpoint.Text.Trim(),OpenAiModel=OpenAiModelEntry.Text.Trim(),
   CompatibleModel=CompatibleModelEntry.Text.Trim(),CompatibleEndpoint=CompatibleEndpointEntry.Text.Trim(),AllowCloudDialogue=CloudConsent.IsChecked==true,
   ModelDirectory=ModelFolderEntry.Text.Trim(),ModelFile=ModelFileEntry.Text.Trim(),
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
  SaveMetadataKeys();settingsStore.Save(settings);
  appSettings=settings;OpenAiKeyEntry.Clear();CompatibleKeyEntry.Clear();RefreshKeyStatus();UpdateConnectionSummary();
  SettingsStatus.Text="Settings saved. Keys are protected for this Windows account.";return settings;
 }
 private void SaveSettings(object sender,RoutedEventArgs e){if(operation==null)try{SaveSettingsCore();}catch(Exception ex){Error(ex);}}
 private void RefreshKeyStatus() {
  RefreshMetadataKeyStatus();
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
 private void BrowseModelFolder(object sender,RoutedEventArgs e){if(PickFolder("Model storage folder") is string p){ModelFolderEntry.Text=p;RefreshLocalModels();}}
 private void BrowseModelFile(object sender,RoutedEventArgs e) {
  var dialog=new OpenFileDialog{Title="Choose GGUF model",Filter="GGUF model (*.gguf)|*.gguf"};
  if(dialog.ShowDialog(this)==true){ModelFileEntry.Text=dialog.FileName;ProviderChoice.SelectedIndex=1;ModelId.Text=Path.GetFileNameWithoutExtension(dialog.FileName);RefreshLocalModels();}
 }
 private void CancelSettingsDownload(object sender,RoutedEventArgs e)=>operation?.Cancel();
 private async void DownloadRuntime(object sender,RoutedEventArgs e)=>await DownloadLocalAssets(false);
 private async void DownloadModel(object sender,RoutedEventArgs e)=>await DownloadLocalAssets(true);
 private async Task DownloadLocalAssets(bool includeModel) {
  if(operation!=null)return;
  try {
   var settings=SaveSettingsCore();
   operation=new();settingsDownload=true;SetBusy(true);
   SettingsCancelButton.Visibility=Visibility.Visible;
   using var http=new HttpClient{Timeout=TimeSpan.FromHours(12)};
   var progress=new Progress<string>(message=>SettingsStatus.Text=message);
   var executable=await RuntimeDownloads.EnsureAsync(settings,http,progress,operation.Token);
   if(includeModel) {
    var profile=(ModelProfile)DownloadProfileChoice.SelectedItem;
    var path=await ModelDownloads.DownloadAsync(profile,settings,http,progress,operation.Token);
    ModelFileEntry.Text=path;ProviderChoice.SelectedIndex=1;ModelId.Text=Path.GetFileNameWithoutExtension(path);RefreshLocalModels();
   }
   SaveSettingsCore();SettingsStatus.Text="Local runtime ready: "+executable+(includeModel?" · Model verified.":"");
  }catch(OperationCanceledException){SettingsStatus.Text="Download cancelled. Partial downloads removed.";}
  catch(Exception ex){SettingsStatus.Text="Download failed: "+ex.Message;}
  finally{operation?.Dispose();operation=null;settingsDownload=false;SetBusy(false);SettingsCancelButton.Visibility=Visibility.Collapsed;RefreshManagedRuntime();}
 }
}
