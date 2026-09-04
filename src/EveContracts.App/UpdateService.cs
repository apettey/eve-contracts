using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace EveContracts.App;

/// <summary>
/// Auto-update via Velopack against this repo's GitHub Releases (CI publishes
/// Velopack packages on every v* tag). Checks on startup and every 6 h; the UI
/// shows an update chip, clicking it downloads (delta when possible) and restarts.
///
/// Only active when the app was installed through the Velopack Setup.exe —
/// dev runs and plain-zip runs are never touched. While the repo is private the
/// unauthenticated check fails quietly and starts working once the repo is public.
/// </summary>
public sealed class UpdateService : BackgroundService
{
    private const string RepoUrl = "https://github.com/apettey/eve-contracts";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly ILogger<UpdateService> _log;
    private readonly UpdateManager _mgr;
    private UpdateInfo? _pending;

    public string? AvailableVersion { get; private set; }
    public string Status { get; private set; } = ""; // "", "available", "downloading", "failed"
    public event Action? Changed;

    public UpdateService(ILogger<UpdateService> log)
    {
        _log = log;
        _mgr = new UpdateManager(new GithubSource(RepoUrl, null, prerelease: false));
    }

    public string CurrentVersion => _mgr.IsInstalled ? _mgr.CurrentVersion?.ToString() ?? "?" : "dev";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_mgr.IsInstalled) return; // dev / zip run — nothing to update
        await Task.Delay(TimeSpan.FromSeconds(20), ct); // let the startup sync go first
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var info = await _mgr.CheckForUpdatesAsync();
                if (info is not null)
                {
                    _pending = info;
                    AvailableVersion = info.TargetFullRelease.Version.ToString();
                    Status = "available";
                    _log.LogInformation("Update available: {Version} (running {Current})", AvailableVersion, CurrentVersion);
                    Changed?.Invoke();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Private repo / offline / rate limit — try again next interval.
                _log.LogDebug("Update check failed: {Error}", ex.Message);
            }
            await Task.Delay(CheckInterval, ct);
        }
    }

    /// <summary>Download the pending update and restart into it.</summary>
    public async Task ApplyAsync()
    {
        if (_pending is null || Status == "downloading") return;
        Status = "downloading";
        Changed?.Invoke();
        try
        {
            await _mgr.DownloadUpdatesAsync(_pending);
            _log.LogInformation("Update {Version} downloaded; restarting", AvailableVersion);
            _mgr.ApplyUpdatesAndRestart(_pending);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Update failed: {Error}", ex.Message);
            Status = "failed";
            Changed?.Invoke();
        }
    }
}
