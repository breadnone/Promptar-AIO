using System.IO;
using System.Windows;

namespace MyAiGen;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MyAiGen_crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Unhandled: {args.Exception}\n");
            }
            catch { }
            args.Handled = true;
            Environment.Exit(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MyAiGen_crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Domain unhandled: {args.ExceptionObject}\n");
            }
            catch { }
            Environment.Exit(1);
        };
        base.OnStartup(e);
    }
}
