using System.IO;
using System.Windows;
using System.Windows.Controls;
using Scout.Core;
namespace Scout.Desktop;

/// <summary>Small show-discovery sample, independent of the later identification scope.</summary>
public sealed class DiscoverySampleWindow:Window
{
    public List<string> SelectedFiles {get;private set;}=[];
    private sealed record Entry(string Path,string Label);
    public DiscoverySampleWindow(string root,IReadOnlyList<string>? selected=null) {
        Title="Determine the show";Width=700;Height=480;MinWidth=520;MinHeight=360;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=(System.Windows.Media.Brush)Application.Current.FindResource("CanvasBrush");
        Foreground=System.Windows.Media.Brushes.White;
        var grid=new Grid{Margin=new Thickness(24)};
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})grid.RowDefinitions.Add(new(){Height=height});
        var heading=new TextBlock{Text="Choose 1 or 2 sample episodes",FontSize=22,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,10)};
        var intro=new TextBlock{Text="One episode is usually enough to start. Add a second for more evidence. Click a file to select or deselect it. You’ll choose which episodes to identify after selecting the show.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16)};
        var files=VideoSelection.Enumerate(root).Select(p=>new Entry(p,Path.GetRelativePath(root,p))).ToList();
        var videos=new ListBox{SelectionMode=SelectionMode.Multiple,ItemsSource=files,DisplayMemberPath="Label"};
        foreach(var entry in files.Where(e=>selected?.Contains(e.Path,StringComparer.OrdinalIgnoreCase)==true).Take(2))videos.SelectedItems.Add(entry);
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)};
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content="Cancel",IsCancel=true,Margin=new Thickness(0,0,12,0)};
        var discover=new Button{Content="Determine show",IsDefault=true};buttons.Children.Add(cancel);buttons.Children.Add(discover);
        void Refresh() {
            var count=videos.SelectedItems.Count;
            status.Text=files.Count==0?"No supported video files found in this source folder.":count==0?"Select at least one sample episode.":count>2?"Choose at most 2 episodes. Click extra selections to remove them.":$"{count} sample episode(s) selected. This does not limit the later episode scan.";
            discover.IsEnabled=count is 1 or 2;
        }
        videos.SelectionChanged+=(_,_)=>Refresh();
        discover.Click+=(_,_)=>{if(videos.SelectedItems.Count is not (1 or 2))return;SelectedFiles=videos.SelectedItems.Cast<Entry>().Select(e=>e.Path).ToList();DialogResult=true;};
        foreach(var (control,row) in new (UIElement,int)[]{(heading,0),(intro,1),(videos,2),(status,3),(buttons,4)}){Grid.SetRow(control,row);grid.Children.Add(control);}
        Content=grid;Refresh();
    }
}
