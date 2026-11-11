using System.Windows;

namespace LightweightBrowser;

public partial class App : Application
{
    public App()
    {
        Velopack.VelopackApp.Build().Run();
        InitializeComponent();
    }
}
