using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scout.Core;
namespace Scout.Desktop;

/// <summary>A catalog suggestion is not an AI verdict; the user chooses which path to take.</summary>
public sealed class SeriesHintWindow:Window
{
    public bool UseAi {get;private set;}
    public ShowCandidate? SelectedCandidate {get;private set;}
    public SeriesHintWindow(DiscoveryResult hints) {
        Title="Confirm the series";Width=760;Height=600;MinWidth=540;MinHeight=450;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=(Brush)Application.Current.FindResource("CanvasBrush");Foreground=Brushes.White;
        var grid=new Grid{Margin=new Thickness(24)};
        foreach(var size in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),new GridLength(1,GridUnitType.Star),GridLength.Auto})
            grid.RowDefinitions.Add(new(){Height=size});
        var heading=new TextBlock{Text=hints.Candidates.Count>0?"Does this look right?":"No clear match from file hints",FontSize=24,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12)};
        var intro=new TextBlock{Text=hints.Candidates.Count>0
            ?"These catalog suggestions come from your folder, filenames and embedded metadata. Confirm the right show to continue, or ask AI to check sample dialogue."
            :"AI can inspect dialogue from up to two videos and search the selected catalog for a matching show.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
        var note=new TextBlock{Text="No model has been started. AI assistance can take several minutes on CPU. Episode identification is a separate step.",TextWrapping=TextWrapping.Wrap,Foreground=(Brush)Application.Current.FindResource("MutedBrush"),Margin=new Thickness(0,0,0,16)};
        var choices=new ListBox{ItemsSource=hints.Candidates,DisplayMemberPath="Label",Margin=new Thickness(0,0,0,12)};
        var details=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,0,0,16)};
        var buttons=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content="Cancel",IsCancel=true};
        var assist=new Button{Content="Use AI to check"};
        var confirm=new Button{Content="Use this show",IsDefault=true,Style=(Style)Application.Current.FindResource("Primary")};
        void Refresh() {
            SelectedCandidate=choices.SelectedItem as ShowCandidate;confirm.IsEnabled=SelectedCandidate!=null;
            details.Text=SelectedCandidate==null?"No catalog suggestion to confirm.":
                (SelectedCandidate.CluesAgree?"Your file hints agree with this catalog entry.\n\n":"Search result from your file hints. Review the title and description.\n\n")+SelectedCandidate.Detail;
        }
        choices.SelectionChanged+=(_,_)=>Refresh();
        choices.SelectedItem=hints.Candidates.FirstOrDefault(c=>c.CluesAgree)??hints.Candidates.FirstOrDefault();
        confirm.Click+=(_,_)=>{if(SelectedCandidate!=null)DialogResult=true;};
        assist.Click+=(_,_)=>{UseAi=true;DialogResult=true;};
        buttons.Children.Add(cancel);buttons.Children.Add(assist);buttons.Children.Add(confirm);
        foreach(var (control,row) in new (UIElement,int)[]{(heading,0),(intro,1),(note,2),(choices,3),(details,4),(buttons,5)}){Grid.SetRow(control,row);grid.Children.Add(control);}
        if(hints.Candidates.Count==0){choices.Visibility=Visibility.Collapsed;details.Visibility=Visibility.Collapsed;confirm.Visibility=Visibility.Collapsed;assist.IsDefault=true;Height=360;MinHeight=320;}
        Content=grid;Refresh();
    }
}
