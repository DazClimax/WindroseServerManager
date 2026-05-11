using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WindroseServerManager.Core.Models;

namespace WindroseServerManager.Core.Services;

/// <summary>
/// Triggered sources for scheduled and automatic restarts. Raised on the scheduler thread.
/// </summary>
public sealed record RestartEvent(RestartTrigger Trigger, string Reason);

public enum RestartTrigger { ScheduledTime, HighRam, MaxUptime, ScheduledWarning }

/// <summary>
/// Hosted service that triggers scheduled + threshold-based restarts. Raises RestartNotified
/// for warnings (so the UI can toast) and actual restart events.
/// </summary>
public sealed class RestartScheduler : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger<RestartScheduler> _logger;
    private readonly IAppSettingsService _settings;
    private readonly IServerProcessService _server;
    private readonly IMetricsService _metrics;
    private readonly IServerEventLog _events;
    private readonly IServerInstallService _installer;
    private readonly IBackupService _backups;
    private readonly IWindrosePlusApiService _windrosePlusApi;

    private DateTime _lastTriggerDate = DateTime.MinValue;
    private DateTime _lastWarnDate = DateTime.MinValue;
    private DateTime _lastAutoRestartUtc = DateTime.MinValue;

    public event Action<RestartEvent>? RestartNotified;

    public RestartScheduler(
        ILogger<RestartScheduler> logger,
        IAppSettingsService settings,
        IServerProcessService server,
        IMetricsService metrics,
        IServerEventLog events,
        IServerInstallService installer,
        IBackupService backups,
        IWindrosePlusApiService windrosePlusApi)
    {
        _logger = logger;
        _settings = settings;
        _server = server;
        _metrics = metrics;
        _events = events;
        _installer = installer;
        _backups = backups;
        _windrosePlusApi = windrosePlusApi;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RestartScheduler started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;

                // Warn-Toast vor geplantem Restart.
                if (ShouldWarnNow(now))
                {
                    var mins = Math.Max(0, _settings.Current.RestartWarnMinutes);
                    await SendRestartBroadcastAsync(mins, "Geplanter Restart.", stoppingToken).ConfigureAwait(false);
                    RestartNotified?.Invoke(new RestartEvent(
                        RestartTrigger.ScheduledWarning,
                        $"Geplanter Restart in {mins} Minuten."));
                    _lastWarnDate = now.Date;
                }

                // Geplanter Restart zum Zeitpunkt.
                if (ShouldTriggerScheduledNow(now))
                {
                    await TriggerRestartAsync(RestartTrigger.ScheduledTime, "Geplanter Restart.", stoppingToken).ConfigureAwait(false);
                    _lastTriggerDate = now.Date;
                }
                else
                {
                    // Schwellen-Checks — nur wenn Server läuft und nicht gerade nach Auto-Restart.
                    if (_server.Status == ServerStatus.Running
                        && (DateTime.UtcNow - _lastAutoRestartUtc).TotalMinutes >= 5)
                    {
                        var (threshold, reason) = CheckAutoRestartThresholds();
                        if (threshold is not null)
                        {
                            await TriggerRestartAsync(threshold.Value, reason, stoppingToken).ConfigureAwait(false);
                            _lastAutoRestartUtc = DateTime.UtcNow;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RestartScheduler loop error");
            }

            try { await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        _logger.LogInformation("RestartScheduler stopped");
    }

    private bool IsDayEnabled(DateTime now)
    {
        var days = _settings.Current.RestartDays;
        if (days is null || days.Count == 0) return true; // leer = täglich
        return days.Contains(now.DayOfWeek);
    }

    private bool ShouldTriggerScheduledNow(DateTime now)
    {
        if (!_settings.Current.ScheduledRestartEnabled) return false;
        if (_server.Status != ServerStatus.Running) return false;
        if (!IsDayEnabled(now)) return false;

        var timeStr = _settings.Current.DailyRestartTime;
        if (!TimeSpan.TryParse(timeStr, out var target)) return false;

        var todayAt = now.Date + target;
        var windowEnd = todayAt.AddMinutes(2);
        if (now < todayAt || now > windowEnd) return false;

        return _lastTriggerDate.Date != now.Date;
    }

    private bool ShouldWarnNow(DateTime now)
    {
        if (!_settings.Current.ScheduledRestartEnabled) return false;
        if (_server.Status != ServerStatus.Running) return false;
        if (!IsDayEnabled(now)) return false;

        var warnMins = _settings.Current.RestartWarnMinutes;
        if (warnMins <= 0) return false;

        var timeStr = _settings.Current.DailyRestartTime;
        if (!TimeSpan.TryParse(timeStr, out var target)) return false;

        var todayAt = now.Date + target;
        var warnAt = todayAt.AddMinutes(-warnMins);
        // 2-min Window ab warnAt; einmal pro Tag.
        if (now < warnAt || now > warnAt.AddMinutes(2)) return false;
        return _lastWarnDate.Date != now.Date;
    }

    private (RestartTrigger? trigger, string reason) CheckAutoRestartThresholds()
    {
        var s = _settings.Current;

        if (s.AutoRestartOnMaxUptimeEnabled && _server.StartedAtUtc is not null)
        {
            var uptime = DateTime.UtcNow - _server.StartedAtUtc.Value;
            if (uptime.TotalHours >= Math.Max(1, s.AutoRestartMaxUptimeHours))
                return (RestartTrigger.MaxUptime, $"Uptime-Grenze erreicht ({(int)uptime.TotalHours}h).");
        }

        if (s.AutoRestartOnHighRamEnabled)
        {
            try
            {
                var host = _metrics.GetHostMetricsAsync().GetAwaiter().GetResult();
                var proc = _metrics.GetServerProcessMetrics();
                if (proc is not null && host.RamTotalBytes > 0)
                {
                    var pct = proc.RamBytes * 100.0 / host.RamTotalBytes;
                    if (pct >= Math.Max(10, s.AutoRestartRamThresholdPercent))
                        return (RestartTrigger.HighRam, $"RAM-Grenze erreicht ({pct:F0} %).");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Threshold-Check RAM fehlgeschlagen");
            }
        }

        return (null, string.Empty);
    }

    private async Task TriggerRestartAsync(RestartTrigger trigger, string reason, CancellationToken ct)
    {
        _logger.LogInformation("Restart trigger={Trigger} reason={Reason}", trigger, reason);
        RestartNotified?.Invoke(new RestartEvent(trigger, reason));
        await SendRestartBroadcastAsync(0, reason, ct).ConfigureAwait(false);

        var eventType = trigger switch
        {
            RestartTrigger.ScheduledTime => ServerEventType.ScheduledRestart,
            RestartTrigger.HighRam => ServerEventType.AutoRestartHighRam,
            RestartTrigger.MaxUptime => ServerEventType.AutoRestartMaxUptime,
            _ => ServerEventType.ScheduledRestart,
        };
        await _events.AppendAsync(new ServerEvent(DateTime.UtcNow, eventType, reason), ct).ConfigureAwait(false);

        try { await _server.StopAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Scheduled restart: stop failed"); }

        var serverServiceStopped = await WaitForServerServiceStoppedAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

        if (_settings.Current.RestartCreateBackupBeforeStart)
        {
            if (serverServiceStopped)
                await CreateBackupBeforeRestartAsync(ct).ConfigureAwait(false);
            else
                await AppendMaintenanceSkippedAsync("Backup vor Restart übersprungen: Server-Prozess läuft noch oder stoppt noch.", ct).ConfigureAwait(false);
        }

        if (_settings.Current.RestartInstallUpdateBeforeStart)
        {
            if (serverServiceStopped)
                await InstallUpdateBeforeRestartAsync(ct).ConfigureAwait(false);
            else
                await AppendMaintenanceSkippedAsync("Server-Update vor Restart übersprungen: Server-Prozess läuft noch oder stoppt noch.", ct).ConfigureAwait(false);
        }

        try
        {
            await _server.StartAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Scheduled restart complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled restart: start failed");
        }
    }

    private async Task<bool> WaitForServerServiceStoppedAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsServerServiceStopped())
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }

        return IsServerServiceStopped();
    }

    private bool IsServerServiceStopped() => _server.Status is ServerStatus.Stopped or ServerStatus.Crashed;

    private Task AppendMaintenanceSkippedAsync(string message, CancellationToken ct)
    {
        _logger.LogWarning("{Message} Status={Status}", message, _server.Status);
        return _events.AppendAsync(new ServerEvent(DateTime.UtcNow, ServerEventType.ScheduledRestart, message), ct);
    }

    private async Task CreateBackupBeforeRestartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Creating backup before automated restart");
        await _events.AppendAsync(new ServerEvent(DateTime.UtcNow, ServerEventType.ScheduledRestart, "Backup vor Restart wird erstellt."), ct)
            .ConfigureAwait(false);

        try
        {
            var backup = await _backups.CreateBackupAsync(isAutomatic: true, ct).ConfigureAwait(false);
            if (backup is null)
            {
                await _events.AppendAsync(new ServerEvent(DateTime.UtcNow, ServerEventType.ScheduledRestart, "Backup vor Restart übersprungen: Save-Verzeichnis nicht gefunden."), ct)
                    .ConfigureAwait(false);
                return;
            }

            var deleted = _backups.ApplyRetention();
            var detail = deleted > 0
                ? $"Backup vor Restart erstellt: {backup.FileName}. Retention hat {deleted} alte Backups gelöscht."
                : $"Backup vor Restart erstellt: {backup.FileName}.";
            await _events.AppendAsync(new ServerEvent(DateTime.UtcNow, ServerEventType.ScheduledRestart, detail), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Automated restart backup failed; starting server anyway");
            await _events.AppendAsync(new ServerEvent(DateTime.UtcNow, ServerEventType.ScheduledRestart, $"Backup vor Restart fehlgeschlagen: {ex.Message}"), ct)
                .ConfigureAwait(false);
        }
    }

    private bool IsWindrosePlusActive(string serverDir)
    {
        if (string.IsNullOrWhiteSpace(serverDir)) return false;
        var full = Path.GetFullPath(serverDir).TrimEnd('\\', '/');
        return _settings.Current.WindrosePlusActiveByServer.GetValueOrDefault(full, false)
            || _settings.Current.WindrosePlusActiveByServer.GetValueOrDefault(full + "\\", false)
            || _settings.Current.WindrosePlusActiveByServer.GetValueOrDefault(serverDir, false);
    }

    private async Task SendRestartBroadcastAsync(int minutes, string reason, CancellationToken ct)
    {
        var s = _settings.Current;
        if (!s.RestartBroadcastEnabled) return;

        var serverDir = _settings.ActiveServerDir;
        if (!IsWindrosePlusActive(serverDir))
        {
            _logger.LogWarning("Restart broadcast skipped: WindrosePlus is not active for {Dir}", serverDir);
            return;
        }

        var template = string.IsNullOrWhiteSpace(s.RestartBroadcastMessage)
            ? "Server restartet in {minutes} Minuten. Grund: {reason}"
            : s.RestartBroadcastMessage;
        var message = template
            .Replace("{minutes}", minutes.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{reason}", reason, StringComparison.OrdinalIgnoreCase);

        try
        {
            var command = _windrosePlusApi.BuildBroadcastCommand(message);
            await _windrosePlusApi.RconAsync(serverDir, command, ct).ConfigureAwait(false);
            _logger.LogInformation("Restart broadcast sent: {Message}", message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restart broadcast failed");
        }
    }

    private async Task InstallUpdateBeforeRestartAsync(CancellationToken ct)
    {
        var installDir = _settings.ActiveServerDir;
        if (string.IsNullOrWhiteSpace(installDir))
        {
            _logger.LogWarning("Automated restart update skipped: active server dir is empty");
            return;
        }

        _logger.LogInformation("Installing server update before automated restart for {Dir}", installDir);
        await _events.AppendAsync(new ServerEvent(DateTime.UtcNow, ServerEventType.ScheduledRestart, "Server-Update vor Restart wird eingespielt."), ct)
            .ConfigureAwait(false);

        try
        {
            await foreach (var progress in _installer.InstallOrUpdateAsync(installDir, ct).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(progress.Message))
                    _logger.LogInformation("Restart update: {Phase} {Message}", progress.Phase, progress.Message);
                if (!string.IsNullOrWhiteSpace(progress.LogLine))
                    _logger.LogDebug("Restart update: {Line}", progress.LogLine);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Automated restart update failed; starting server anyway");
            await _events.AppendAsync(new ServerEvent(DateTime.UtcNow, ServerEventType.ScheduledRestart, $"Server-Update vor Restart fehlgeschlagen: {ex.Message}"), ct)
                .ConfigureAwait(false);
        }
    }
}
