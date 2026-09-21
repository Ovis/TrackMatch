using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using TrackMatch.App.Playback;
using TrackMatch.App.Settings;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.App;

/// <summary>
/// WPFとGeneric Hostのライフサイクルを接続し、アプリケーション共通サービスとログ基盤を構成する。
/// </summary>
public partial class App : System.Windows.Application
{
    private IHost? _host;

    /// <summary>
    /// Generic HostとSerilogを初期化してMain Windowを表示する。
    /// </summary>
    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = await new JsonAppSettingsStore().LoadAsync();
        var dataDirectory = Path.GetDirectoryName(TrackMatchDataPaths.DefaultDatabasePath)
            ?? throw new InvalidOperationException("TrackMatchのデータ保存Directoryを特定できない。");
        var logDirectory = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        var minimumLevel = settings.DetailedLogging ? LogEventLevel.Debug : LogEventLevel.Information;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logDirectory, "trackmatch-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<ISynchronizedPlaybackService, NAudioSynchronizedPlaybackService>();
                    services.AddSingleton<MainWindowViewModel>();
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            await _host.StartAsync();
            _host.Services.GetRequiredService<MainWindow>().Show();
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "TrackMatchの起動に失敗した");
            throw;
        }
    }

    /// <summary>
    /// WPF終了時にHostとLoggerを確実に停止・破棄する。
    /// </summary>
    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
        }
        finally
        {
            await Log.CloseAndFlushAsync();
            base.OnExit(e);
        }
    }
}
