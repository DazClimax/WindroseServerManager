using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using WindroseServerManager.App.Services;
using WindroseServerManager.App.Views.Dialogs;
using WindroseServerManager.Core.Models;
using WindroseServerManager.Core.Services;

namespace WindroseServerManager.App.ViewModels;

public partial class EditorViewModel : ViewModelBase
{
    private readonly IWindrosePlusApiService _api;
    private readonly IWindrosePlusService _wplus;
    private readonly IAppSettingsService _settings;
    private readonly IServerProcessService _proc;
    private readonly IToastService _toasts;

    [ObservableProperty] private ObservableCollection<CategoryGroup> _categories = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;

    public bool IsWindrosePlusActive =>
        _settings.Current.WindrosePlusActiveByServer.GetValueOrDefault(
            _settings.ActiveServerDir ?? string.Empty, false);

    public bool HasAnyError => Categories.Any(c => c.Entries.Any(e => e.HasError));
    public bool CanSave => !HasAnyError && !IsLoading;

    public EditorViewModel(IWindrosePlusApiService api, IWindrosePlusService wplus, IAppSettingsService settings, IServerProcessService proc, IToastService toasts)
    {
        _api = api;
        _wplus = wplus;
        _settings = settings;
        _proc = proc;
        _toasts = toasts;
    }

    public void Start()
    {
        OnPropertyChanged(nameof(IsWindrosePlusActive));
        if (IsWindrosePlusActive) _ = LoadAsync();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var serverDir = _settings.ActiveServerDir;
        if (string.IsNullOrWhiteSpace(serverDir)) return;

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var cfg = await Task.Run(() => _api.ReadConfig(serverDir));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Categories.Clear();
                var groups = new Dictionary<string, List<ConfigEntryViewModel>>();
                foreach (var schema in WindrosePlusConfigSchema.All)
                {
                    object? initial = null;
                    if (cfg is not null)
                    {
                        GetSection(cfg, schema).TryGetValue(schema.Key, out initial);
                    }
                    var entry = new ConfigEntryViewModel(schema, initial);
                    entry.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(ConfigEntryViewModel.HasError))
                        {
                            OnPropertyChanged(nameof(HasAnyError));
                            OnPropertyChanged(nameof(CanSave));
                            SaveCommand.NotifyCanExecuteChanged();
                        }
                    };
                    if (!groups.TryGetValue(schema.Category, out var list))
                        groups[schema.Category] = list = new List<ConfigEntryViewModel>();
                    list.Add(entry);
                }
                foreach (var (cat, entries) in groups)
                    Categories.Add(new CategoryGroup(cat, entries));
                OnPropertyChanged(nameof(HasAnyError));
                OnPropertyChanged(nameof(CanSave));
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Editor load failed");
            ErrorMessage = Loc.Get("Editor.Error.Load");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(CanSave));
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    private static Dictionary<string, object?> GetSection(WindrosePlusConfig cfg, ConfigEntrySchema schema)
    {
        var section = schema.JsonSection ?? schema.Category.ToLowerInvariant();
        return section switch
        {
            "rcon"        => cfg.Rcon,
            "multipliers" => cfg.Multipliers,
            _             => cfg.Server,
        };
    }

    [RelayCommand]
    private async Task InstallWindrosePlusAsync()
    {
        var dir = _settings.ActiveServerDir;
        if (string.IsNullOrWhiteSpace(dir)) return;
        var top = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (top is null) return;
        var bannerVm = new RetrofitBannerViewModel(dir, _wplus, _api, _settings, _toasts);
        var dialog = new WindroseServerManager.App.Views.Dialogs.RetrofitDialog { DataContext = bannerVm };
        var confirmed = await dialog.ShowDialog<bool>(top);
        if (confirmed)
        {
            OnPropertyChanged(nameof(IsWindrosePlusActive));
            Start();
        }
    }

    [RelayCommand]
    private void ResetAll()
    {
        foreach (var group in Categories)
            foreach (var entry in group.Entries)
                entry.ResetCommand.Execute(null);
    }

    private bool CanExecuteSave() => CanSave;

    [RelayCommand(CanExecute = nameof(CanExecuteSave))]
    private async Task SaveAsync()
    {
        var serverDir = _settings.ActiveServerDir;
        if (string.IsNullOrWhiteSpace(serverDir)) return;
        if (HasAnyError)
        {
            _toasts.Error(Loc.Get("Editor.ValidationError"));
            return;
        }

        var cfg = _api.ReadConfig(serverDir) ?? new WindrosePlusConfig();
        foreach (var group in Categories)
            foreach (var entry in group.Entries)
                GetSection(cfg, entry.Schema)[entry.Key] = entry.ToTypedValue();

        try
        {
            await _api.WriteConfigAsync(serverDir, cfg, CancellationToken.None);
            await SyncWindrosePlusSettingsAsync(serverDir, cfg);
            _toasts.Success(Loc.Get("Editor.Saved"));

            if (_proc.Status == ServerStatus.Running)
                _toasts.Warning(Loc.Get("Editor.RestartRequired"));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Editor save failed");
            _toasts.Error(Loc.Get("Editor.Error.Save"));
        }
    }

    private async Task SyncWindrosePlusSettingsAsync(string serverDir, WindrosePlusConfig cfg)
    {
        var hasPort = TryReadInt(cfg.Server.GetValueOrDefault("http_port"), out var port) && port > 0;
        var password = TryReadString(cfg.Rcon.GetValueOrDefault("password"));
        if (!hasPort && string.IsNullOrWhiteSpace(password)) return;

        await _settings.UpdateAsync(s =>
        {
            var normalizedDir = System.IO.Path.GetFullPath(serverDir).TrimEnd('\\', '/');
            if (hasPort)
            {
                s.WindrosePlusDashboardPortByServer[serverDir] = port;
                s.WindrosePlusDashboardPortByServer[normalizedDir] = port;
            }
            if (!string.IsNullOrWhiteSpace(password))
            {
                s.WindrosePlusRconPasswordByServer[serverDir] = password;
                s.WindrosePlusRconPasswordByServer[normalizedDir] = password;
            }
        });
    }

    private static string? TryReadString(object? value) =>
        value switch
        {
            string s => s,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } el => el.GetString(),
            _ => null
        };

    private static bool TryReadInt(object? value, out int result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                result = (int)l;
                return true;
            case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } el when el.TryGetInt32(out var parsed):
                result = parsed;
                return true;
            case string s when int.TryParse(s, out var parsedString):
                result = parsedString;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}

public sealed class CategoryGroup
{
    public string Category { get; }
    public string CategoryDisplayName => Loc.Get($"Editor.Category.{Category}");
    public IReadOnlyList<ConfigEntryViewModel> Entries { get; }

    public CategoryGroup(string category, IReadOnlyList<ConfigEntryViewModel> entries)
    {
        Category = category;
        Entries = entries;
    }
}
