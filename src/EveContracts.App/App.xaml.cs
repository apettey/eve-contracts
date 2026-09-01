using System.Net.Http;
using System.Windows;
using EveContracts.Core;
using EveContracts.Core.Data;
using EveContracts.Core.Esi;
using EveContracts.Core.Sde;
using EveContracts.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveContracts.App;

public partial class App : Application
{
    public static IHost AppHost { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                AppHost?.Services.GetService<ILogger<App>>()?.LogError(args.Exception, "Unhandled UI exception");
            }
            catch { }
            MessageBox.Show(args.Exception.Message, "Contract Tracker — error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppHost = Host.CreateDefaultBuilder()
            .ConfigureLogging(l =>
            {
                l.SetMinimumLevel(LogLevel.Information);
                l.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
                l.AddFilter("System.Net.Http", LogLevel.Warning);
                l.AddProvider(new FileLoggerProvider());
            })
            .ConfigureServices(services =>
            {
                services.AddWpfBlazorWebView();
#if DEBUG
                services.AddBlazorWebViewDeveloperTools();
#endif
                services.AddDbContext<AppDb>(o => o
                    .UseSqlite($"Data Source={AppPaths.DbPath}")
                    .AddInterceptors(new EveContracts.Core.Data.SqlitePragmaInterceptor()));

                services.AddHttpClient("esi", c =>
                {
                    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "EveContracts/1.0 (admin@pettey.me)");
                    c.Timeout = TimeSpan.FromSeconds(60);
                }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                });
                services.AddHttpClient("fuzzwork", c =>
                {
                    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "EveContracts/1.0 (admin@pettey.me)");
                    c.Timeout = TimeSpan.FromSeconds(120);
                }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip,
                });
                services.AddHttpClient("sde", c =>
                {
                    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "EveContracts/1.0 (admin@pettey.me)");
                    c.Timeout = TimeSpan.FromMinutes(10);
                }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                });
                services.AddHttpClient("sso", c =>
                {
                    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "EveContracts/1.0 (admin@pettey.me)");
                });

                services.AddSingleton(sp => new EsiClient(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("esi"),
                    sp.GetRequiredService<ILogger<EsiClient>>()));
                services.AddSingleton<SettingsService>();
                services.AddSingleton<StaticDataCache>();
                services.AddSingleton<SdeService>();
                services.AddSingleton<PriceService>();
                services.AddSingleton<PublicContractSync>();
                services.AddSingleton<EsiAuthService>();
                services.AddSingleton<OwnContractSync>();
                services.AddSingleton<ContractQueryService>();
                services.AddSingleton<SyncScheduler>();
                services.AddHostedService(sp => sp.GetRequiredService<SyncScheduler>());
                services.AddSingleton<UiState>();
                services.AddSingleton<AlertSoundService>();
            })
            .Build();

        await AppHost.StartAsync();

        var sounds = AppHost.Services.GetRequiredService<AlertSoundService>();
        AppHost.Services.GetRequiredService<OwnContractSync>().NewInboundContracts += _ => sounds.PlayNewContract();
        AppHost.Services.GetRequiredService<PublicContractSync>().BigProfitFound += (_, _) => sounds.PlayProfitAlert();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (AppHost is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await AppHost.StopAsync(cts.Token); } catch { }
            AppHost.Dispose();
        }
        base.OnExit(e);
    }
}
