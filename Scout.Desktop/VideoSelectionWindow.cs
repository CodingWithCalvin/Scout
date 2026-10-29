using System.IO;
using System.Windows;
using System.Windows.Controls;
using Scout.Core;
namespace Scout.Desktop;

public sealed class VideoSelectionWindow:Window
{
    private readonly List<string> files;
    private int scopeIndex;
    private bool refreshing;
    private readonly List<Entry> entries;
    private readonly WrapPanel scope=new(){Margin=new Thickness(0,12,0,14)};
    private readonly ComboBox seasons=new(){Margin=new Thickness(0,0,0,10)};
    private readonly ListBox videos=new(){SelectionMode=SelectionMode.Extended};
    private readonly TextBlock count=new(){Margin=new Thickness(0,10,0,10)};
    private readonly Button start=new(){Content="Use this selection",HorizontalAlignment=HorizontalAlignment.Right};
    public List<string> SelectedFiles {get;private set;}=[];
    private sealed record Entry(string Path,string Label);
    public VideoSelectionWindow(string root,IReadOnlyList<string>? selected=null) {
        Title="Choose episodes to identify";Width=820;Height=640;MinWidth=600;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=(System.Windows.Media.Brush)Application.Current.FindResource("CanvasBrush");
        Foreground=System.Windows.Media.Brushes.White;
        files=VideoSelection.Enumerate(root);
        entries=files.Select(p=>new Entry(p,Path.GetRelativePath(root,p))).ToList();
        var grid=new Grid{Margin=new Thickness(24)};
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})grid.RowDefinitions.Add(new(){Height=height});
        var intro=new TextBlock{Text="Choose which files to include. Use Ctrl/Shift for custom multi-selection. Selecting files does not start AI processing.",TextWrapping=TextWrapping.Wrap};grid.Children.Add(intro);
        foreach(var (label,index) in new[]{"One episode","One season / folder","Full show","Custom multi-select"}.Select((label,index)=>(label,index))) {
            var option=new RadioButton{Content=label,GroupName="Scope",IsChecked=index==0,Margin=new Thickness(0,0,20,8)};
            option.Checked+=(_,_)=>{scopeIndex=index;Refresh();};scope.Children.Add(option);
        }
        seasons.ItemsSource=files.Select(p=>VideoSelection.SeasonFolder(root,p)).Distinct(StringComparer.OrdinalIgnoreCase).Select(p=>new Entry(p,Path.GetRelativePath(root,p))).ToList();seasons.DisplayMemberPath="Label";seasons.SelectedIndex=0;
        videos.ItemsSource=entries;videos.DisplayMemberPath="Label";videos.SelectedIndex=files.Count>0?0:-1;
        foreach(var (control,row) in new (UIElement,int)[]{(scope,1),(seasons,2),(videos,3),(count,4),(start,5)}){Grid.SetRow(control,row);grid.Children.Add(control);}
        Content=grid;
        if(selected?.Count>0) {
            var saved=videos.Items.Cast<Entry>().Where(e=>selected.Contains(e.Path,StringComparer.OrdinalIgnoreCase)).ToList();
            scopeIndex=selected.Count==1?0:3;((RadioButton)scope.Children[scopeIndex]).IsChecked=true;
            if(scopeIndex==0)videos.SelectedItem=saved.FirstOrDefault();
            else {videos.SelectedItems.Clear();foreach(var entry in saved)videos.SelectedItems.Add(entry);}
        }
        seasons.SelectionChanged+=(_,_)=>Refresh();videos.SelectionChanged+=(_,_)=>Refresh();
        start.Click+=(_,_)=>{SelectedFiles=Selection();if(SelectedFiles.Count>0)DialogResult=true;};Refresh();
    }
    private List<string> Selection()=>VideoSelection.Scope(files,scopeIndex switch{0=>"episode",1=>"season",2=>"show",_=>"selected"},(videos.SelectedItem as Entry)?.Path,(seasons.SelectedItem as Entry)?.Path,videos.SelectedItems.Cast<Entry>().Select(e=>e.Path).ToList());
    private void Refresh() {
        if(refreshing)return;
        refreshing=true;
        try {
            seasons.Visibility=scopeIndex==1?Visibility.Visible:Visibility.Collapsed;
            var previous=videos.SelectedItems.Cast<Entry>().Select(e=>e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var included=scopeIndex is 1 or 2?Selection().ToHashSet(StringComparer.OrdinalIgnoreCase):previous;
            var visible=scopeIndex==1?entries.Where(e=>included.Contains(e.Path)).ToList():entries;
            videos.SelectionMode=scopeIndex==0?SelectionMode.Single:SelectionMode.Extended;
            if(!videos.Items.Cast<Entry>().SequenceEqual(visible)) {
                videos.ItemsSource=visible;
                if(scopeIndex==0)videos.SelectedItem=visible.FirstOrDefault(e=>previous.Contains(e.Path))??visible.FirstOrDefault();
                else foreach(var entry in visible.Where(e=>included.Contains(e.Path)))videos.SelectedItems.Add(entry);
            }
            if(scopeIndex is 1 or 2)videos.SelectAll();
            var total=Selection().Count;
            count.Text=scopeIndex switch {
                1=>$"All {total} videos in this season / folder are included. Only those files are shown.",
                2=>$"All {total} videos in this show are included.",
                _=>$"{total} of {files.Count} videos selected. Source files stay unchanged."
            };
            videos.ToolTip=scopeIndex is 1 or 2?"Every highlighted file is included. Choose Custom multi-select to exclude individual episodes.":null;
            start.IsEnabled=total>0;
        } finally {refreshing=false;}
    }
}
