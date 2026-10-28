using System.Windows;
using Scout.Core;
namespace Scout.Desktop;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        try {
            if(e.Args.FirstOrDefault()=="--preview") {
                if(e.Args.Length!=6)throw new ArgumentException("--preview report.json root series output catalog.json");
                var plan=PreviewPlanner.Create(Data.Read(e.Args[1]),e.Args[2],e.Args[3],Data.Catalog(e.Args[5]));
                PreviewPlanner.Write(plan,e.Args[4]);Shutdown(0);return;
            }
            var window=new MainWindow();MainWindow=window;
            if(e.Args.Length==2&&e.Args[0]=="--report")window.LoadReport(e.Args[1]);
            window.Show();
        }catch(Exception ex){MessageBox.Show(ex.Message,"Scout",MessageBoxButton.OK,MessageBoxImage.Error);Shutdown(1);}
    }
}