using System.Windows;
using System.Windows.Threading;

namespace VocalPracticeRecorder;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled (domain)", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task", args.Exception);
            args.SetObserved();
        };
        if (e.Args.Length > 0 && e.Args[0] == "--selftest")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = SelfTest.RunAsync(e.Args.Skip(1).ToArray()).ContinueWith(t => Dispatcher.Invoke(() => Shutdown(t.Result)));
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--make-icon")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(IconMaker.Run(e.Args.Skip(1).ToArray()));
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--snapshot")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.InvokeAsync(async () =>
            {
                int code;
                try { code = await Snapshot.RunAsync(e.Args.Skip(1).ToArray()); }
                catch (Exception ex) { Log.Error("snapshot", ex); code = 1; }
                Shutdown(code);
            });
            return;
        }
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled", e.Exception);
        MessageBox.Show(e.Exception.Message, "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
