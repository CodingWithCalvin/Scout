using System.Windows;
using Scout.Core;
namespace Scout.Desktop;

public partial class MainWindow
{
    private void LoadNamingControls()
    {
        DefaultNamingMode.SelectedIndex=appSettings.Naming.PreserveCorrectNames?0:1;
        DefaultFileTemplate.Text=appSettings.Naming.FileTemplate;
        DefaultSeasonTemplate.Text=appSettings.Naming.SeasonTemplate;
        NamingChanged(this,new RoutedEventArgs());
    }
    private NamingOptions DefaultNaming()=>new(){PreserveCorrectNames=DefaultNamingMode.SelectedIndex!=1,FileTemplate=DefaultFileTemplate.Text.Trim(),SeasonTemplate=DefaultSeasonTemplate.Text.Trim()};
    private void NamingChanged(object sender,RoutedEventArgs e)
    {
        if(DefaultNamingExample==null)return;
        string Example(NamingOptions options) {
            try {options.Validate();var episode=new Episode(1,1,1,"Pilot");var identity=workspace?.SeriesIdentity??new SeriesIdentity("tvdb","12345","Example Show");return options.Folder(identity.Name,episode,identity)+"\\"+options.FileName(identity.Name,episode,identity)+".mkv";}
            catch(ArgumentException ex){return ex.Message;}
        }
        DefaultNamingExample.Text=Example(DefaultNaming());
    }
    private void SaveNamingAndPreview(object sender,RoutedEventArgs e)
    {
        if(operation!=null)return;
        try {SaveSettingsCore();if(report!=null)GeneratePreview(sender,e);else SettingsStatus.Text="Naming saved. Import or identify episodes to generate a preview.";}
        catch(Exception ex){Error(ex);}
    }
}
