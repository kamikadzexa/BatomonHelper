using System.Windows;
namespace BatomonHelper.Training;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window=new MainWindow();
        if(e.Args is ["--render-play",var playPath]) {window.RenderPlayPreview(playPath);Shutdown();}
        else if(e.Args is ["--render",var path]) { window.RenderPreview(path); Shutdown(); }
        else if(e.Args is ["--render-history",var imagePath,var recordPath]) {window.RenderHistory(imagePath,recordPath);Shutdown();}
        else {if(e.Args is ["--output",var directory])window.OpenOutput(directory);window.Show();}
    }
}
