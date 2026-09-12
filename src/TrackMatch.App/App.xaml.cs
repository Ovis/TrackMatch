using System.Windows.Threading;
using TrackMatch.App.Diagnostics;

namespace TrackMatch.App;

/// <summary>
/// TrackMatch GUIアプリケーションの起動処理と最上位例外監視を担う。
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        AppFileLogger.Info($"Application startup. Version={Environment.Version}, BaseDirectory={AppContext.BaseDirectory}, Log={AppFileLogger.CurrentLogPath}");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
        AppFileLogger.Info("Application OnStartup completed.");
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        AppFileLogger.Info($"Application exit. ExitCode={e.ApplicationExitCode}");

        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppFileLogger.Error("Unhandled exception on WPF Dispatcher.", e.Exception);

        // 原因調査中は既存の終了動作を変えず、例外を握りつぶさない。
        e.Handled = false;
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppFileLogger.Error($"Unhandled AppDomain exception. IsTerminating={e.IsTerminating}", exception);
            return;
        }

        AppFileLogger.Info($"Unhandled AppDomain exception without Exception instance. IsTerminating={e.IsTerminating}, Value={e.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        => AppFileLogger.Error("Unobserved task exception.", e.Exception);
}
