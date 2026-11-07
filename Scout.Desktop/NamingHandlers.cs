using System.Windows;
using Scout.Core;
namespace Scout.Desktop;

public partial class MainWindow
{
    private void LoadNamingControls()
    {
        DefaultNamingMode.SelectedIndex=appSettings.Naming.PreserveCorrectNames?0:1;
        DefaultPathTemplate.Text=appSettings.Naming.EditablePathTemplate();
        NamingChanged(this,new RoutedEventArgs());
    }
    private void SuggestTemplate(object sender,RoutedEventArgs e)=>DefaultPathTemplate.ShowSuggestions();
    private void TemplateEdited(object? sender,EventArgs e) {
        NamingChanged(sender??this,new RoutedEventArgs());
        if(TemplateValidation==null||DefaultPathTemplate==null)return;
        try {
            NamingDsl.Validate(DefaultPathTemplate.Text);
            TemplateValidation.Text="Valid template";
            TemplateValidation.Foreground=(System.Windows.Media.Brush)FindResource("AccentBrush");
        }catch(ArgumentException ex) {
            TemplateValidation.Text=ex.Message;
            TemplateValidation.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,146,146));
        }
    }
    private NamingOptions DefaultNaming()=>new(){PreserveCorrectNames=DefaultNamingMode.SelectedIndex!=1,PathTemplate=DefaultPathTemplate.Text.Trim()};
    private void NamingChanged(object sender,RoutedEventArgs e)
    {
        if(DefaultNamingExample==null||DefaultPathTemplate==null)return;
        string Example(NamingOptions options) {
            try {options.Validate();var episode=new Episode(1,1,1,"Tori & Jade’s Playdate!");var identity=workspace?.SeriesIdentity??new SeriesIdentity("tvdb","12345","Example Show");return options.Folder(identity.Name,episode,identity)+"\\"+options.FileName(identity.Name,episode,identity)+".mkv\nCombined: "+options.AssignmentName(identity.Name,[episode,new Episode(2,1,2,"The Next Story")],identity:identity)+".mkv\nSplit: "+options.AssignmentName(identity.Name,[episode],1,2,identity)+".mkv";}
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
