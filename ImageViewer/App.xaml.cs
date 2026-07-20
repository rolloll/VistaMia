using System.Configuration;
using System.Data;
using System.Windows;

namespace ImageViewer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Windows passes the target file as argv[0] for "Open with" / file-association launches.
        var initialFilePath = e.Args.Length > 0 ? e.Args[0] : null;
        new MainWindow(initialFilePath).Show();
    }
}

