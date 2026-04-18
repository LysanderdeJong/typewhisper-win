using System.Collections.ObjectModel;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Aggregates all sub-view models and controls routed navigation inside the settings shell.
/// </summary>
public sealed partial class SettingsWindowViewModel : ObservableObject
{
    private static SettingsRoute _lastOpenedRoute = SettingsRoute.Dashboard;

    public SettingsViewModel Settings { get; }
    public ModelManagerViewModel ModelManager { get; }
    public HistoryViewModel History { get; }
    public DictionaryViewModel Dictionary { get; }
    public SnippetsViewModel Snippets { get; }
    public ProfilesViewModel Profiles { get; }
    public DashboardViewModel Dashboard { get; }
    public PluginsViewModel Plugins { get; }
    public PromptsViewModel Prompts { get; }
    public AudioRecorderViewModel Recorder { get; }
    public bool IsMemoryFeatureEnabled => FeatureFlags.Memory;
    public bool IsHttpApiFeatureEnabled => FeatureFlags.HttpApi;
    public FileTranscriptionViewModel FileTranscription { get; }

    private readonly UpdateService _updateService;
    private readonly IErrorLogService _errorLog;

    [ObservableProperty] private UserControl? _currentSection;
    [ObservableProperty] private SettingsRoute _currentRoute = _lastOpenedRoute;
    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private int _pendingFileImporterRequestId;

    public string CurrentAppVersion => _updateService.CurrentVersion;
    public ObservableCollection<ErrorLogEntry> ErrorLogEntries { get; } = [];
    public bool HasErrorLogEntries => ErrorLogEntries.Count > 0;
    public ObservableCollection<SettingsNavigationGroup> NavigationGroups { get; } = [];

    private readonly Dictionary<SettingsRoute, Func<UserControl>> _sectionFactories = [];
    private readonly Dictionary<SettingsRoute, UserControl> _sectionCache = [];
    private readonly Dictionary<SettingsRoute, SettingsNavigationItem> _navigationLookup = [];

    public SettingsWindowViewModel(
        SettingsViewModel settings,
        ModelManagerViewModel modelManager,
        HistoryViewModel history,
        DictionaryViewModel dictionary,
        SnippetsViewModel snippets,
        ProfilesViewModel profiles,
        DashboardViewModel dashboard,
        PluginsViewModel plugins,
        PromptsViewModel prompts,
        AudioRecorderViewModel recorder,
        FileTranscriptionViewModel fileTranscription,
        UpdateService updateService,
        IErrorLogService errorLog)
    {
        Settings = settings;
        ModelManager = modelManager;
        History = history;
        Dictionary = dictionary;
        Snippets = snippets;
        Profiles = profiles;
        Dashboard = dashboard;
        Plugins = plugins;
        Prompts = prompts;
        Recorder = recorder;
        FileTranscription = fileTranscription;
        _updateService = updateService;
        _errorLog = errorLog;
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                BuildNavigation();
                SyncNavigationSelection();
            });
        };

        BuildNavigation();
        RefreshErrorLog();
        _errorLog.EntriesChanged += RefreshErrorLog;
        SyncNavigationSelection();
    }

    [RelayCommand]
    private async Task NavigateToRoute(SettingsRoute route)
    {
        Open(route);
        if (route == SettingsRoute.History)
            await History.LoadAsync();
    }

    [RelayCommand]
    private Task NavigateToItem(SettingsNavigationItem? item)
    {
        if (item is null)
            return Task.CompletedTask;

        return NavigateToRoute(item.Route);
    }

    [RelayCommand]
    private void OpenFileImporter()
    {
        PendingFileImporterRequestId++;
        Open(SettingsRoute.FileTranscription);
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        UpdateStatusText = Loc.Instance["Update.Checking"];

        await _updateService.CheckForUpdatesAsync();

        IsCheckingForUpdates = false;
        if (_updateService.IsUpdateAvailable)
        {
            IsUpdateAvailable = true;
            UpdateStatusText = Loc.Instance.GetString("Update.AvailableFormat", _updateService.AvailableVersion ?? "");
        }
        else
        {
            IsUpdateAvailable = false;
            UpdateStatusText = Loc.Instance["Update.UpToDate"];
        }
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        UpdateStatusText = Loc.Instance["Update.Downloading"];
        await _updateService.DownloadAndApplyAsync();
        UpdateStatusText = Loc.Instance["Update.Failed"];
    }

    [RelayCommand]
    private void ClearErrorLog()
    {
        _errorLog.ClearAll();
    }

    [RelayCommand]
    private void ExportDiagnostics()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"typewhisper-diagnostics-{DateTime.Now:yyyy-MM-dd-HHmmss}.json",
            DefaultExt = ".json",
            Filter = "JSON|*.json"
        };

        if (dialog.ShowDialog() == true)
        {
            var json = _errorLog.ExportDiagnostics();
            System.IO.File.WriteAllText(dialog.FileName, json);
        }
    }

    [RelayCommand]
    private void OpenSetupWizard()
    {
        var window = App.Services.GetRequiredService<WelcomeWindow>();
        window.Show();
    }

    public void RegisterSection(SettingsRoute route, Func<UserControl> factory)
    {
        _sectionFactories[route] = factory;
    }

    public void NavigateToDefault()
    {
        Open(_lastOpenedRoute);
    }

    public void Open(SettingsRoute route)
    {
        if (!_sectionFactories.ContainsKey(route))
            return;

        if (!_sectionCache.TryGetValue(route, out var section))
        {
            section = _sectionFactories[route]();
            _sectionCache[route] = section;
        }

        CurrentSection = section;
        CurrentRoute = route;
        _lastOpenedRoute = route;

        if (route is SettingsRoute.Dictation or SettingsRoute.Integrations)
            ModelManager.RefreshPluginAvailability();
    }

    public bool TryConsumePendingFileImporterRequest()
    {
        if (PendingFileImporterRequestId == 0)
            return false;

        PendingFileImporterRequestId = 0;
        return true;
    }

    partial void OnCurrentRouteChanged(SettingsRoute value)
    {
        SyncNavigationSelection();
    }

    private void RefreshErrorLog()
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            ErrorLogEntries.Replace(_errorLog.Entries);
            OnPropertyChanged(nameof(HasErrorLogEntries));
        });
    }

    private void BuildNavigation()
    {
        NavigationGroups.Clear();
        _navigationLookup.Clear();

        NavigationGroups.Add(CreateGroup(SettingsGroup.Overview, Loc.Instance["SettingsGroup.Overview"],
        [
            new SettingsNavigationItem(SettingsRoute.Dashboard, Loc.Instance["Nav.Dashboard"], "\uE80F")
        ]));

        NavigationGroups.Add(CreateGroup(SettingsGroup.Capture, Loc.Instance["SettingsGroup.Capture"],
        [
            new SettingsNavigationItem(SettingsRoute.Dictation, Loc.Instance["Nav.Dictation"], "\uE720"),
            new SettingsNavigationItem(SettingsRoute.Shortcuts, Loc.Instance["Nav.Shortcuts"], "\uE765"),
            new SettingsNavigationItem(SettingsRoute.FileTranscription, Loc.Instance["Nav.FileTranscription"], "\uE8A5"),
            new SettingsNavigationItem(SettingsRoute.Recorder, Loc.Instance["Nav.Recorder"], "\uE189")
        ]));

        NavigationGroups.Add(CreateGroup(SettingsGroup.Library, Loc.Instance["SettingsGroup.Library"],
        [
            new SettingsNavigationItem(SettingsRoute.History, Loc.Instance["Nav.History"], "\uE81C"),
            new SettingsNavigationItem(SettingsRoute.Dictionary, Loc.Instance["Nav.Dictionary"], "\uE8D2"),
            new SettingsNavigationItem(SettingsRoute.Snippets, Loc.Instance["Nav.Snippets"], "\uE8C8"),
            new SettingsNavigationItem(SettingsRoute.Profiles, Loc.Instance["Nav.Profiles"], "\uE77B")
        ]));

        NavigationGroups.Add(CreateGroup(SettingsGroup.AI, Loc.Instance["SettingsGroup.AI"],
        [
            new SettingsNavigationItem(SettingsRoute.Prompts, Loc.Instance["Nav.Prompts"], "\uE8FD"),
            new SettingsNavigationItem(SettingsRoute.Integrations, Loc.Instance["Nav.Plugins"], "\uE943")
        ]));

        NavigationGroups.Add(CreateGroup(SettingsGroup.System, Loc.Instance["SettingsGroup.System"],
        [
            new SettingsNavigationItem(SettingsRoute.General, Loc.Instance["Nav.General"], "\uE713"),
            new SettingsNavigationItem(SettingsRoute.Appearance, Loc.Instance["Nav.Appearance"], "\uE790"),
            new SettingsNavigationItem(SettingsRoute.Advanced, Loc.Instance["Nav.Advanced"], "\uE9CE"),
            new SettingsNavigationItem(SettingsRoute.License, Loc.Instance["Nav.License"], "\uE72E"),
            new SettingsNavigationItem(SettingsRoute.About, Loc.Instance["Nav.About"], "\uE946")
        ]));
    }

    private SettingsNavigationGroup CreateGroup(SettingsGroup group, string title, IReadOnlyList<SettingsNavigationItem> items)
    {
        foreach (var item in items)
            _navigationLookup[item.Route] = item;

        return new SettingsNavigationGroup(group, title, items);
    }

    private void SyncNavigationSelection()
    {
        foreach (var item in _navigationLookup.Values)
            item.IsSelected = item.Route == CurrentRoute;
    }

}
