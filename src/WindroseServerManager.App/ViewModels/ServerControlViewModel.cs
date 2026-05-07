using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindroseServerManager.App.Services;
using WindroseServerManager.App.Views.Dialogs;
using WindroseServerManager.Core.Models;
using WindroseServerManager.Core.Services;

namespace WindroseServerManager.App.ViewModels;

public partial class ServerControlViewModel : ViewModelBase, IDisposable
{
    private readonly IServerProcessService _proc;
    private readonly IAppSettingsService _settings;
    private readonly IServerConfigService _config;
    private readonly IToastService _toasts;
    private readonly IServerEventLog _eventLog;
    private readonly System.Timers.Timer _refreshTimer;

    public ObservableCollection<ServerEvent> Events { get; } = new();

    [ObservableProperty] private ServerStatus _status;
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _scheduledRestartEnabled;
    [ObservableProperty] private string _dailyRestartTime = "04:00";
    [ObservableProperty] private int _restartWarnMinutes = 5;
    [ObservableProperty] private bool _restartMon, _restartTue, _restartWed, _restartThu, _restartFri, _restartSat, _restartSun;
    [ObservableProperty] private bool _restartInstallUpdateBeforeStart;
    [ObservableProperty] private bool _restartBroadcastEnabled;
    [ObservableProperty] private string _restartBroadcastMessage = string.Empty;

    [ObservableProperty] private bool _autoRestartOnHighRamEnabled;
    [ObservableProperty] private int _autoRestartRamThresholdPercent = 80;
    [ObservableProperty] private bool _autoRestartOnMaxUptimeEnabled;
    [ObservableProperty] private int _autoRestartMaxUptimeHours = 24;
    [ObservableProperty] private string? _inviteCode;
    [ObservableProperty] private bool _isBusy;

    public bool CanOpenServerDir => !string.IsNullOrWhiteSpace(_settings.ActiveServerDir)
                                    && Directory.Exists(_settings.ActiveServerDir);

    public bool CanOpenServerDescription
    {
        get
        {
            var p = _config.GetServerDescriptionPath();
            return !string.IsNullOrWhiteSpace(p) && File.Exists(p);
        }
    }

    public TimeSpan DailyRestartTimeSpan
    {
        get
        {
            if (TimeSpan.TryParseExact(DailyRestartTime, @"hh\:mm", CultureInfo.InvariantCulture, out var ts))
                return ts;
            if (TimeSpan.TryParse(DailyRestartTime, CultureInfo.InvariantCulture, out ts))
                return ts;
            return TimeSpan.FromHours(4);
        }
        set
        {
            DailyRestartTime = value.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        }
    }

    public ServerControlViewModel(IServerProcessService proc, IAppSettingsService settings, IServerConfigService config, IToastService toasts, IServerEventLog eventLog, ILocalizationService localization)
    {
        _proc = proc;
        _settings = settings;
        _config = config;
        _toasts = toasts;
        _eventLog = eventLog;
        _proc.StatusChanged += OnStatus;
        _eventLog.Appended += OnEventAppended;
        _status = _proc.Status;

        _ = LoadEventsAsync();

        ScheduledRestartEnabled = settings.Current.ScheduledRestartEnabled;
        DailyRestartTime = settings.Current.DailyRestartTime;
        RestartWarnMinutes = settings.Current.RestartWarnMinutes;
        RestartInstallUpdateBeforeStart = settings.Current.RestartInstallUpdateBeforeStart;
        RestartBroadcastEnabled = settings.Current.RestartBroadcastEnabled;
        RestartBroadcastMessage = string.IsNullOrWhiteSpace(settings.Current.RestartBroadcastMessage)
            ? "Server restartet in {minutes} Minuten. Grund: {reason}"
            : settings.Current.RestartBroadcastMessage;

        var days = settings.Current.RestartDays ?? new List<DayOfWeek>();
        // Leere Liste = täglich → alle Tage aktiv.
        var allDays = days.Count == 0;
        RestartMon = allDays || days.Contains(DayOfWeek.Monday);
        RestartTue = allDays || days.Contains(DayOfWeek.Tuesday);
        RestartWed = allDays || days.Contains(DayOfWeek.Wednesday);
        RestartThu = allDays || days.Contains(DayOfWeek.Thursday);
        RestartFri = allDays || days.Contains(DayOfWeek.Friday);
        RestartSat = allDays || days.Contains(DayOfWeek.Saturday);
        RestartSun = allDays || days.Contains(DayOfWeek.Sunday);

        AutoRestartOnHighRamEnabled = settings.Current.AutoRestartOnHighRamEnabled;
        AutoRestartRamThresholdPercent = settings.Current.AutoRestartRamThresholdPercent;
        AutoRestartOnMaxUptimeEnabled = settings.Current.AutoRestartOnMaxUptimeEnabled;
        AutoRestartMaxUptimeHours = settings.Current.AutoRestartMaxUptimeHours;

        _ = LoadInviteCodeAsync();

        _refreshTimer = new System.Timers.Timer(5000);
        _refreshTimer.Elapsed += async (_, _) => await LoadInviteCodeAsync();
        _refreshTimer.Start();
    }

    partial void OnDailyRestartTimeChanged(string value)
    {
        OnPropertyChanged(nameof(DailyRestartTimeSpan));
    }

    private async Task LoadInviteCodeAsync()
    {
        try
        {
            var desc = await _config.LoadServerDescriptionAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                InviteCode = string.IsNullOrWhiteSpace(desc?.InviteCode) ? null : desc!.InviteCode;
                OnPropertyChanged(nameof(CanOpenServerDir));
                OnPropertyChanged(nameof(CanOpenServerDescription));
            });
        }
        catch { }
    }

    [RelayCommand]
    private async Task CopyInviteCodeAsync()
    {
        if (string.IsNullOrWhiteSpace(InviteCode)) return;
        var top = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d ? d.MainWindow : null;
        if (top?.Clipboard is null) return;
        await top.Clipboard.SetTextAsync(InviteCode);
        _toasts.Success(Loc.Format("Toast.InviteCopiedFormat", InviteCode));
    }

    [RelayCommand]
    private void OpenServerDir()
    {
        var path = _settings.ActiveServerDir;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void OpenServerDescription()
    {
        var path = _config.GetServerDescriptionPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch { }
    }

    private void OnStatus(ServerStatus s) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        Status = s;
        UpdateUptime();
    });

    private void UpdateUptime()
    {
        if (_proc.StartedAtUtc is null) { UptimeText = "—"; return; }
        var t = DateTime.UtcNow - _proc.StartedAtUtc.Value;
        UptimeText = t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";
    }

    private static Avalonia.Controls.Window? GetOwnerWindow()
    {
        return Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
                ? d.MainWindow
                : null;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        ErrorMessage = _proc.ValidateCanStart();
        if (ErrorMessage is not null) { _toasts.Warning(ErrorMessage); return; }
        try { await _proc.StartAsync(); _toasts.Success(Loc.Get("Toast.ServerStarting")); }
        catch (Exception ex) { var msg = ErrorMessageHelper.FriendlyMessage(ex); ErrorMessage = msg; _toasts.Error(msg); }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        try { await _proc.StopAsync(); _toasts.Info(Loc.Get("Toast.ServerStopping")); }
        catch (Exception ex) { var msg = ErrorMessageHelper.FriendlyMessage(ex); ErrorMessage = msg; _toasts.Error(msg); }
    }

    [RelayCommand]
    private async Task KillAsync()
    {
        // Kein Confirm-Dialog wenn der Server bereits (fast) aus ist.
        if (_proc.Status is ServerStatus.Running or ServerStatus.Starting)
        {
            var owner = GetOwnerWindow();
            if (owner is not null)
            {
                var confirmed = await ConfirmDialog.ShowAsync(
                    owner,
                    Loc.Get("Confirm.Kill.Title"),
                    Loc.Get("Confirm.Kill.Message"),
                    confirmLabel: Loc.Get("Confirm.Kill.Label"),
                    danger: true);
                if (!confirmed) return;
            }
        }

        try { await _proc.KillAsync(); _toasts.Warning(Loc.Get("Toast.ServerKilled")); }
        catch (Exception ex) { var msg = ErrorMessageHelper.FriendlyMessage(ex); ErrorMessage = msg; _toasts.Error(msg); }
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            _toasts.Info(Loc.Get("Toast.RestartInProgress"));

            try { await _proc.StopAsync(); }
            catch (Exception ex) { var msg = ErrorMessageHelper.FriendlyMessage(ex); ErrorMessage = msg; _toasts.Error(msg); return; }

            // Polling: max 10s, alle 500ms
            var maxWait = TimeSpan.FromSeconds(10);
            var step = TimeSpan.FromMilliseconds(500);
            var waited = TimeSpan.Zero;
            while (_proc.Status != ServerStatus.Stopped && waited < maxWait)
            {
                await Task.Delay(step);
                waited += step;
            }

            if (_proc.Status != ServerStatus.Stopped)
            {
                _toasts.Warning(Loc.Get("Toast.StopTooSlow"));
                return;
            }

            ErrorMessage = _proc.ValidateCanStart();
            if (ErrorMessage is not null) { _toasts.Warning(ErrorMessage); return; }

            try { await _proc.StartAsync(); _toasts.Success(Loc.Get("Toast.ServerRestarting")); }
            catch (Exception ex) { var msg = ErrorMessageHelper.FriendlyMessage(ex); ErrorMessage = msg; _toasts.Error(msg); }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearSessionHistoryAsync()
    {
        await _eventLog.ClearAsync().ConfigureAwait(false);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Events.Clear());
        _toasts.Info(Loc.Get("Toast.SessionHistoryCleared"));
    }

    [RelayCommand]
    private async Task SaveRestartScheduleAsync()
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                DailyRestartTime ?? string.Empty,
                @"^\d{2}:\d{2}$",
                System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromSeconds(1)))
        {
            _toasts.Warning(Loc.Get("Toast.TimeFormatInvalid"));
            return;
        }

        var days = new List<DayOfWeek>();
        if (RestartMon) days.Add(DayOfWeek.Monday);
        if (RestartTue) days.Add(DayOfWeek.Tuesday);
        if (RestartWed) days.Add(DayOfWeek.Wednesday);
        if (RestartThu) days.Add(DayOfWeek.Thursday);
        if (RestartFri) days.Add(DayOfWeek.Friday);
        if (RestartSat) days.Add(DayOfWeek.Saturday);
        if (RestartSun) days.Add(DayOfWeek.Sunday);

        await _settings.UpdateAsync(s =>
        {
            s.ScheduledRestartEnabled = ScheduledRestartEnabled;
            s.DailyRestartTime = DailyRestartTime;
            s.RestartWarnMinutes = Math.Max(0, RestartWarnMinutes);
            s.RestartInstallUpdateBeforeStart = RestartInstallUpdateBeforeStart;
            s.RestartBroadcastEnabled = RestartBroadcastEnabled;
            s.RestartBroadcastMessage = string.IsNullOrWhiteSpace(RestartBroadcastMessage)
                ? "Server restartet in {minutes} Minuten. Grund: {reason}"
                : RestartBroadcastMessage.Trim();
            // 7 von 7 Tagen aktiv ist semantisch "täglich" → leere Liste speichern.
            s.RestartDays = days.Count == 7 ? new List<DayOfWeek>() : days;
            s.AutoRestartOnHighRamEnabled = AutoRestartOnHighRamEnabled;
            s.AutoRestartRamThresholdPercent = Math.Clamp(AutoRestartRamThresholdPercent, 10, 100);
            s.AutoRestartOnMaxUptimeEnabled = AutoRestartOnMaxUptimeEnabled;
            s.AutoRestartMaxUptimeHours = Math.Max(1, AutoRestartMaxUptimeHours);
        });
        _toasts.Success(Loc.Get("Toast.AutomationSaved"));
    }

    private async Task LoadEventsAsync()
    {
        var list = await _eventLog.ReadRecentAsync(50).ConfigureAwait(false);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Events.Clear();
            foreach (var e in list) Events.Add(e);
        });
    }

    private void OnEventAppended(ServerEvent evt) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Events.Insert(0, evt);
            while (Events.Count > 50) Events.RemoveAt(Events.Count - 1);
        });

    public void Dispose()
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _proc.StatusChanged -= OnStatus;
        _eventLog.Appended -= OnEventAppended;
    }
}
