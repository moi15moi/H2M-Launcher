﻿using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reactive;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using H2MLauncher.Core.Game;
using H2MLauncher.Core.Game.Models;
using H2MLauncher.Core.IW4MAdmin.Models;
using H2MLauncher.Core.Matchmaking;
using H2MLauncher.Core.Models;
using H2MLauncher.Core.Networking.GameServer;
using H2MLauncher.Core.Networking.GameServer.HMW;
using H2MLauncher.Core.Services;
using H2MLauncher.Core.Settings;
using H2MLauncher.Core.Utilities;
using H2MLauncher.UI.Dialog;
using H2MLauncher.UI.Dialog.Views;
using H2MLauncher.UI.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Nogic.WritableOptions;

namespace H2MLauncher.UI.ViewModels;

public partial class ServerBrowserViewModel : ObservableObject, IDisposable
{
    private readonly IMasterServerService _h2mMaster;
    private readonly IMasterServerService _hmwMaster;
    private readonly IGameServerInfoService<ServerConnectionDetails> _udpGameServerCommunicationService;
    private readonly IGameServerInfoService<ServerConnectionDetails> _tcpGameServerCommunicationService;
    private readonly H2MCommunicationService _h2MCommunicationService;
    private readonly LauncherService _h2MLauncherService;
    private readonly IClipBoardService _clipBoardService;
    private readonly ISaveFileService _saveFileService;
    private readonly IErrorHandlingService _errorHandlingService;
    private readonly DialogService _dialogService;
    private readonly IMapsProvider _mapsProvider;
    private readonly ILogger<ServerBrowserViewModel> _logger;

    private readonly IWritableOptions<H2MLauncherSettings> _h2MLauncherOptions;
    private readonly IOptions<ResourceSettings> _resourceSettings;

    private CancellationTokenSource _loadCancellation = new();
    private readonly MatchmakingService _matchmakingService;
    private readonly CachedServerDataService _serverDataService;
    private readonly H2MLauncherSettings _defaultSettings;

    private readonly Dictionary<string, string> _mapMap = [];
    private readonly Dictionary<string, string> _gameTypeMap = [];

    private IReadOnlyList<ServerData> _serverData = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateLauncherCommand))]
    private string _updateStatusText = "";

    [ObservableProperty]
    private double _updateDownloadProgress = 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateLauncherCommand))]
    private bool _updateFinished;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecentsSelected))]
    private IServerTabViewModel _selectedTab;

    [ObservableProperty]
    private IServerConnectionDetails? _lastServer = null;
    private SecureString? _lastServerPassword = null;


    [ObservableProperty]
    private GameStateViewModel _gameState = new();

    [ObservableProperty]
    private ServerFilterViewModel _advancedServerFilter;

    [ObservableProperty]
    private ShortcutsViewModel _shortcuts;

    [ObservableProperty]
    private PasswordViewModel _passwordViewModel = new();

    [ObservableProperty]
    private SocialsViewModel _socials = new();

    public bool IsRecentsSelected => SelectedTab.TabName == RecentsTab.TabName;

    public bool IsMatchmakingEnabled =>
        _h2MCommunicationService.GameDetection.IsGameDetectionRunning &&
        _h2MLauncherOptions.CurrentValue.GameMemoryCommunication &&
        _h2MLauncherOptions.CurrentValue.ServerQueueing;

    private ServerTabViewModel<ServerViewModel> AllServersTab { get; set; }
    private ServerTabViewModel<ServerViewModel> HMWServersTab { get; set; }
    private ServerTabViewModel<ServerViewModel> H2MServersTab { get; set; }
    private ServerTabViewModel<ServerViewModel> FavouritesTab { get; set; }
    private ServerTabViewModel<ServerViewModel> RecentsTab { get; set; }
    public ObservableCollection<IServerTabViewModel> ServerTabs { get; set; } = [];


    public event Action? ServerFilterChanged;

    public IAsyncRelayCommand RefreshServersCommand { get; }
    public IAsyncRelayCommand CheckUpdateStatusCommand { get; }
    public IRelayCommand LaunchH2MCommand { get; }
    public IRelayCommand CopyToClipBoardCommand { get; }
    public IRelayCommand SaveServersCommand { get; }
    public IAsyncRelayCommand UpdateLauncherCommand { get; }
    public IRelayCommand OpenReleaseNotesCommand { get; }
    public IRelayCommand RestartCommand { get; }
    public IRelayCommand ShowServerFilterCommand { get; }
    public IRelayCommand ShowSettingsCommand { get; }
    public IAsyncRelayCommand ReconnectCommand { get; }
    public IAsyncRelayCommand DisconnectCommand { get; }
    public IAsyncRelayCommand EnterMatchmakingCommand { get; }



    public ServerBrowserViewModel(
        [FromKeyedServices("H2M")] IMasterServerService h2mMasterService,
        [FromKeyedServices("HMW")] IMasterServerService hmwMasterService,        
        [FromKeyedServices("UDP")] IGameServerInfoService<ServerConnectionDetails> udpGameServerService,
        [FromKeyedServices("TCP")] IGameServerInfoService<ServerConnectionDetails> tcpGameServerService,
        H2MCommunicationService h2MCommunicationService,
        LauncherService h2MLauncherService,
        IClipBoardService clipBoardService,
        ILogger<ServerBrowserViewModel> logger,
        ISaveFileService saveFileService,
        IErrorHandlingService errorHandlingService,
        DialogService dialogService,
        IWritableOptions<H2MLauncherSettings> h2mLauncherOptions,
        IOptions<ResourceSettings> resourceSettings,
        [FromKeyedServices(Constants.DefaultSettingsKey)] H2MLauncherSettings defaultSettings,
        MatchmakingService matchmakingService,
        CachedServerDataService serverDataService,
        IMapsProvider mapsProvider)
    {
        _h2mMaster = h2mMasterService;
        _hmwMaster = hmwMasterService;
        _udpGameServerCommunicationService = udpGameServerService;
        _tcpGameServerCommunicationService = tcpGameServerService;
        _h2MCommunicationService = h2MCommunicationService;
        _h2MLauncherService = h2MLauncherService;
        _clipBoardService = clipBoardService;
        _logger = logger;
        _saveFileService = saveFileService;
        _errorHandlingService = errorHandlingService;
        _dialogService = dialogService;
        _h2MLauncherOptions = h2mLauncherOptions;
        _defaultSettings = defaultSettings;
        _resourceSettings = resourceSettings;
        _matchmakingService = matchmakingService;
        _serverDataService = serverDataService;
        _mapsProvider = mapsProvider;

        RefreshServersCommand = new AsyncRelayCommand(LoadServersAsync);
        LaunchH2MCommand = new RelayCommand(LaunchH2M);
        CheckUpdateStatusCommand = new AsyncRelayCommand(CheckUpdateStatusAsync);
        CopyToClipBoardCommand = new RelayCommand<ServerViewModel>(DoCopyToClipBoardCommand);
        SaveServersCommand = new AsyncRelayCommand(SaveServersAsync);
        UpdateLauncherCommand = new AsyncRelayCommand(DoUpdateLauncherCommand, () => UpdateStatusText != "");
        OpenReleaseNotesCommand = new RelayCommand(DoOpenReleaseNotesCommand);
        RestartCommand = new RelayCommand(DoRestartCommand);
        ShowServerFilterCommand = new RelayCommand(ShowServerFilter);
        ShowSettingsCommand = new RelayCommand(ShowSettings);
        ReconnectCommand = new AsyncRelayCommand(ReconnectServer);
        DisconnectCommand = new AsyncRelayCommand(DisconnectServer);
        EnterMatchmakingCommand = new AsyncRelayCommand(EnterMatchmaking, () => IsMatchmakingEnabled);

        AdvancedServerFilter = new(_resourceSettings.Value, _defaultSettings.ServerFilter);
        Shortcuts = new();

        if (!TryAddNewTab("All Servers", out ServerTabViewModel? allServersTab))
        {
            throw new Exception("Could not add all servers tab");
        }

        if (!TryAddNewTab("H2M Servers", out ServerTabViewModel? h2mServersTab))
        {
            throw new Exception("Could not add H2M servers tab");
        }

        if (!TryAddNewTab("HMW Servers", out ServerTabViewModel? hmwServersTab))
        {
            throw new Exception("Could not add HMW servers tab");
        }

        if (!TryAddNewTab("Favourites", out ServerTabViewModel? favouritesTab))
        {
            throw new Exception("Could not add favourites tab");
        }

        RecentsTab = new RecentServerTabViewModel(JoinServer, AdvancedServerFilter.ApplyFilter)
        {
            ToggleFavouriteCommand = new RelayCommand<ServerViewModel>(ToggleFavorite)
        };

        if (!TryAddNewTab(RecentsTab))
        {
            throw new Exception("Could not add recents tab");
        }

        ServerTabs.Remove(allServersTab);
        AllServersTab = allServersTab;
        H2MServersTab = h2mServersTab;
        HMWServersTab = hmwServersTab;
        FavouritesTab = favouritesTab;

        SelectedTab = ServerTabs.First();

        foreach (IW4MObjectMap oMap in resourceSettings.Value.MapPacks.SelectMany(mappack => mappack.Maps))
        {
            _mapMap!.TryAdd(oMap.Name, oMap.Alias);
        }

        foreach (IW4MObjectMap oMap in resourceSettings.Value.GameTypes)
        {
            _gameTypeMap!.TryAdd(oMap.Name, oMap.Alias);
        }

        H2MLauncherSettings oldSettings = _h2MLauncherOptions.CurrentValue;
        _h2MLauncherOptions.OnChange((newSettings, _) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (!oldSettings.IW4MMasterServerUrl.Equals(newSettings.IW4MMasterServerUrl))
                {
                    // refresh servers when master server url changes
                    RefreshServersCommand.Execute(null);
                }

                // reset filter to stored values
                AdvancedServerFilter.ResetViewModel(newSettings.ServerFilter);

                // reset shortcuts to stored values
                Shortcuts.ResetViewModel(newSettings.KeyBindings);

                OnPropertyChanged(nameof(IsMatchmakingEnabled));
                EnterMatchmakingCommand.NotifyCanExecuteChanged();

                oldSettings = newSettings;
            });
        });

        // initialize server filter view model with stored values
        AdvancedServerFilter.ResetViewModel(_h2MLauncherOptions.CurrentValue.ServerFilter);

        // initialize shortcut key bindings with stored values
        Shortcuts.ResetViewModel(_h2MLauncherOptions.CurrentValue.KeyBindings);

        _h2MCommunicationService.GameDetection.GameDetected += H2MCommunicationService_GameDetected;
        _h2MCommunicationService.GameDetection.GameExited += H2MCommunicationService_GameExited;
        _h2MCommunicationService.GameDetection.Error += GameDetection_Error;
        _h2MCommunicationService.GameCommunication.GameStateChanged += H2MCommunicationService_GameStateChanged;
        _h2MCommunicationService.GameCommunication.Stopped += H2MGameCommunication_Stopped;
        _matchmakingService.Joined += MatchmakingService_Joined;
        _mapsProvider.MapsChanged += MapsProvider_InstalledMapsChanged;
    }

    private void MapsProvider_InstalledMapsChanged(IMapsProvider provider)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var serverViewModel in AllServersTab.Servers)
            {
                serverViewModel.HasMap = provider.InstalledMaps.Contains(serverViewModel.Map);
            }
        });
    }

    private void H2MGameCommunication_Stopped(Exception? exception)
    {
        GameState.State = null;

        if (exception is null)
        {
            return;
        }

        string dialogText;
        MessageBoxButton dialogButtons;
        if (_h2MCommunicationService.GameDetection.DetectedGame is not null)
        {
            dialogText = "It seems like the game communication has crashed. Click 'OK' to restart it.";
            dialogButtons = MessageBoxButton.OKCancel;
        }
        else
        {
            dialogText = "It seems like the game communication has crashed. It will be restarted when the game is detected.";
            dialogButtons = MessageBoxButton.OK;
        }

        if (_dialogService.OpenTextDialog("Error", dialogText, dialogButtons) == true)
        {
            _h2MCommunicationService.StartGameCommunication();
        }
    }

    private void H2MCommunicationService_GameStateChanged(GameState newState)
    {
        GameState.State = newState;
    }

    private void H2MCommunicationService_GameDetected(DetectedGame detectedGame)
    {
        GameState.DetectedGame = detectedGame;
    }

    private void H2MCommunicationService_GameExited()
    {
        GameState.DetectedGame = null;
    }

    private void GameDetection_Error(Exception? obj)
    {
        if (_dialogService.OpenTextDialog("Error",
            "It seems like the game detection has crashed. Would you like to restart it?",
            MessageBoxButton.YesNo) == true)
        {
            _h2MCommunicationService.GameDetection.StartGameDetection();
        }
    }

    private void ShowSettings()
    {
        SettingsViewModel settingsViewModel = new(_h2MLauncherOptions);

        // make sure all active hotkeys are disabled when settings are open
        foreach (var shortcut in Shortcuts.Shortcuts)
        {
            shortcut.IsHotkeyEnabled = false;
        }

        if (_dialogService.OpenDialog<SettingsDialogView>(settingsViewModel) == true)
        {
            // settings saved;
        }

        // re-enable hotkeys
        foreach (var shortcut in Shortcuts.Shortcuts)
        {
            shortcut.IsHotkeyEnabled = true;
        }
    }

    private void ShowServerFilter()
    {
        if (_dialogService.OpenDialog<FilterDialogView>(AdvancedServerFilter) == true)
        {
            ServerFilterChanged?.Invoke();
            StatusText = "Server filter applied.";

            // save to settings
            _h2MLauncherOptions.Update(_h2MLauncherOptions.CurrentValue with
            {
                ServerFilter = AdvancedServerFilter.ToSettings()
            });
        }
    }

    private void OnServerFilterClosed(object? sender, RequestCloseEventArgs e)
    {
        if (e.DialogResult == true)
        {
            StatusText = "Server filter applied.";
        }
    }

    private bool TryAddNewTab<TServerViewModel>(IServerTabViewModel<TServerViewModel> tabViewModel)
        where TServerViewModel : ServerViewModel
    {
        if (ServerTabs.Any(tab => tab.TabName.Equals(tabViewModel.TabName, StringComparison.Ordinal)))
        {
            return false;
        }

        ServerTabs.Add(tabViewModel);
        return true;
    }

    private bool TryAddNewTab(string tabName, [MaybeNullWhen(false)] out ServerTabViewModel tabViewModel)
    {
        if (ServerTabs.Any(tab => tab.TabName.Equals(tabName, StringComparison.Ordinal)))
        {
            tabViewModel = null;
            return false;
        }

        tabViewModel = new ServerTabViewModel(tabName, JoinServer, AdvancedServerFilter.ApplyFilter)
        {
            ToggleFavouriteCommand = new RelayCommand<ServerViewModel>(ToggleFavorite),
        };

        ServerTabs.Add(tabViewModel);
        return true;
    }

    // Method to get the user's favorites from the settings.
    public List<SimpleServerInfo> GetFavoritesFromSettings()
    {
        return _h2MLauncherOptions.CurrentValue.FavouriteServers;
    }

    // Method to get user's recent servers from settings.
    public List<RecentServerInfo> GetRecentsFromSettings()
    {
        return _h2MLauncherOptions.CurrentValue.RecentServers;
    }

    // Method to add a favorite to the settings.
    public void AddFavoriteToSettings(SimpleServerInfo favorite)
    {
        List<SimpleServerInfo> favorites = GetFavoritesFromSettings();

        // Add the new favorite to the list.
        favorites.Add(favorite);

        // Save the updated list to the settings.
        SaveFavorites(favorites);
    }

    // Method to add a recent to the settings.
    public void AddOrUpdateRecentServerInSettings(RecentServerInfo recent)
    {
        List<RecentServerInfo> recents = GetRecentsFromSettings();

        int recentLimit = 30;

        // Remove existing servers with the same IP and port
        int removed = recents.RemoveAll(s => s.ServerIp == recent.ServerIp && s.ServerPort == recent.ServerPort);

        // Add the server with the updated date to the start of the list.
        // If the list exceeds the max size, remove the oldest entries (which are now at the end)
        recents = [recent, .. recents.OrderByDescending(r => r.Joined).Take(recentLimit - 1)]; ;

        // Save the updated list to the settings.
        SaveRecents(recents);
    }

    // Method to remove a favorite from the settings.
    public void RemoveFavoriteFromSettings(string serverIp, int serverPort)
    {
        List<SimpleServerInfo> favorites = GetFavoritesFromSettings();

        // Remove the favorite that matches the provided ServerIp.
        favorites.RemoveAll(fav => fav.ServerIp == serverIp && fav.ServerPort == serverPort);

        // Save the updated list to the settings.
        SaveFavorites(favorites);
    }

    // Private method to save the list of favorites to the settings.
    private void SaveFavorites(List<SimpleServerInfo> favorites)
    {
        _h2MLauncherOptions.Update(_h2MLauncherOptions.CurrentValue with
        {
            FavouriteServers = favorites
        }, true);
    }

    // Private method to save the list of recents to the settings.
    private void SaveRecents(List<RecentServerInfo> recents)
    {
        _h2MLauncherOptions.Update(settings =>
        {
            return settings with { RecentServers = recents };
        }, true);
    }

    private void ToggleFavorite(ServerViewModel? server)
    {
        if (server is null)
            return;

        server.IsFavorite = !server.IsFavorite;

        if (server.IsFavorite)
        {
            // Add to favorites
            AddFavoriteToSettings(new SimpleServerInfo
            {
                ServerIp = server.Ip,
                ServerName = server.HostName,
                ServerPort = server.Port
            });

            // Add to FavoriteServers collection if not already added
            if (!FavouritesTab.Servers.Any(s => s.Ip == server.Ip && s.Port == server.Port))
            {
                FavouritesTab.Servers.Add(server);
            }

            return;
        }

        // Remove from favorites
        RemoveFavoriteFromSettings(server.Ip, server.Port);

        // Remove from FavoriteServers collection
        FavouritesTab.Servers.Remove(server);
    }

    private void UpdateRecentJoinTime(ServerViewModel? server, DateTime joinedTime)
    {
        if (server is null)
            return;

        server.Joined = joinedTime;

        // Update in settings
        AddOrUpdateRecentServerInSettings(new RecentServerInfo
        {
            ServerIp = server.Ip,
            ServerName = server.HostName,
            ServerPort = server.Port,
            Joined = joinedTime
        });

        // Add to RecentServers collection if not already added
        if (!RecentsTab.Servers.Any(s => s.Ip == server.Ip && s.Port == server.Port))
        {
            RecentsTab.Servers.Add(server);
        }

        return;
    }

    private void DoRestartCommand()
    {
        Process.Start(LauncherService.LauncherPath);
        Process.GetCurrentProcess().Kill();
    }

    private void DoOpenReleaseNotesCommand()
    {
        string destinationurl = "https://github.com/Bowhza/H2M-Launcher/releases/latest";
        ProcessStartInfo sInfo = new(destinationurl)
        {
            UseShellExecute = true,
        };
        Process.Start(sInfo);
    }

    private Task<bool> DoUpdateLauncherCommand()
    {
        return _h2MLauncherService.UpdateLauncherToLatestVersion((double progress) =>
        {
            UpdateDownloadProgress = progress;
            if (progress == 100)
            {
                UpdateFinished = true;
            }
        }, CancellationToken.None);
    }

    private void DoCopyToClipBoardCommand(ServerViewModel? server)
    {
        if (server is null)
        {
            if (SelectedTab.SelectedServer is null)
                return;

            server = SelectedTab.SelectedServer;
        }

        string textToCopy = $"connect {server.Ip}:{server.Port}";
        _clipBoardService.SaveToClipBoard(textToCopy);

        StatusText = $"Copied to clipboard";
    }

    public bool ServerFilter(ServerViewModel server)
    {
        return AdvancedServerFilter.ApplyFilter(server);
    }

    private async Task SaveServersAsync()
    {
        // Create a list of "Ip:Port" strings
        List<string> ipPortList = SelectedTab.Servers.Where(ServerFilter)
                                         .Select(server => $"{server.Ip}:{server.Port}")
                                         .ToList();

        // Serialize the list into JSON format
        string jsonString = JsonSerializer.Serialize(ipPortList, JsonContext.Default.ListString);

        try
        {
            // Store the server list into the corresponding directory
            _logger.LogDebug("Storing server list into \"/players2/favourites.json\"");

            string directoryPath = "players2";

            if (!string.IsNullOrEmpty(_h2MLauncherOptions.CurrentValue.MWRLocation))
            {
                string? gameDirectory = Path.GetDirectoryName(_h2MLauncherOptions.CurrentValue.MWRLocation);

                directoryPath = Path.Combine(gameDirectory ?? "", directoryPath);
            }

            string fileName;

            if (!Directory.Exists(directoryPath))
            {
                // let user choose
                fileName = await _saveFileService.SaveFileAs("favourites.json", "JSON file (*.json)|*.json") ?? "";
                if (string.IsNullOrEmpty(fileName))
                    return;
            }
            else
            {
                fileName = Path.Combine(directoryPath, "favourites.json");
            }

            await File.WriteAllTextAsync(fileName, jsonString);

            _logger.LogInformation("Stored server list into {fileName}", fileName);

            StatusText = $"{ipPortList.Count} servers saved to {Path.GetFileName(fileName)}";
        }
        catch (Exception ex)
        {
            _errorHandlingService.HandleException(ex, "Could not save favourites.json file. Make sure the exe is inside the root of the game folder.");
        }
    }

    private async Task CheckUpdateStatusAsync()
    {
        bool isUpToDate = await _h2MLauncherService.IsLauncherUpToDateAsync(CancellationToken.None);
        UpdateStatusText = isUpToDate ? $"" : $"New version available: {_h2MLauncherService.LatestKnownVersion}!";
    }

    private Task UpdateServerDataList(CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            try
            {
                IReadOnlyList<ServerData>? serverData = await _serverDataService.GetServerDataList(cancellationToken);
                if (serverData is not null)
                {
                    _serverData = serverData;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching server data from matchmaking server.");
            }
        });
    }

    private async Task GetServerInfo(
        IGameServerInfoService<ServerConnectionDetails> service, 
        IEnumerable<ServerConnectionDetails> servers, 
        CancellationToken cancellationToken)
    {
        IAsyncEnumerable<(ServerConnectionDetails, GameServerInfo?)> responses = await service.GetInfoAsync(
            servers,
            sendSynchronously: false,
            cancellationToken: cancellationToken);

        // Start by sending info requests to the game servers
        // NOTE: we are using Task.Run to run this in a background thread,
        // because the non async timer blocks the UI
        await Task.Run(async () =>
        {
            try
            {
                await foreach ((ServerConnectionDetails server, GameServerInfo? info) in responses.ConfigureAwait(false).WithCancellation(cancellationToken))
                {
                    if (info is not null)
                    {
                        Application.Current.Dispatcher.Invoke(
                            () => OnGameServerInfoReceived(server, info),
                            DispatcherPriority.Render,
                            cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // canceled
            }
        }, CancellationToken.None);
    }

    private async Task LoadServersAsync()
    {
        await _loadCancellation.CancelAsync();

        _loadCancellation = new();
        using CancellationTokenSource timeoutCancellation = new(10000);
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _loadCancellation.Token, timeoutCancellation.Token);

        try
        {
            StatusText = "Refreshing servers...";

            AllServersTab.Servers.Clear();
            H2MServersTab.Servers.Clear();
            HMWServersTab.Servers.Clear();
            FavouritesTab.Servers.Clear();
            RecentsTab.Servers.Clear();

            // Get servers from the master
            IReadOnlySet<ServerConnectionDetails> hmwServers = await _hmwMaster.FetchServersAsync(linkedCancellation.Token);
            IReadOnlySet<ServerConnectionDetails> h2mServers = await _h2mMaster.FetchServersAsync(linkedCancellation.Token);

            // Exclude HMW only servers from H2M list
            List<ServerConnectionDetails> actualH2mServers = h2mServers.Except(hmwServers).ToList();

            Task hmwServerInfoTask = GetServerInfo(_tcpGameServerCommunicationService, hmwServers, linkedCancellation.Token);
            Task h2mServerInfoTask = GetServerInfo(_udpGameServerCommunicationService, actualH2mServers, linkedCancellation.Token);

            // artificial delay
            await Task.WhenAny(Task.WhenAll(hmwServerInfoTask, h2mServerInfoTask), Task.Delay(1000));

            // Start fetching server data in the background
            _ = UpdateServerDataList(linkedCancellation.Token);

            StatusText = "Ready";
        }
        catch (OperationCanceledException ex)
        {
            // canceled
            Debug.WriteLine($"LoadServersAsync cancelled: {ex.Message}");
        }
    }

    private void OnGameServerInfoReceived(ServerConnectionDetails server, GameServerInfo serverInfo)
    {
        List<SimpleServerInfo> userFavorites = GetFavoritesFromSettings();
        List<RecentServerInfo> userRecents = GetRecentsFromSettings();

        bool isFavorite = userFavorites.Any(fav => fav.ServerIp == server.Ip && fav.ServerPort == server.Port);
        RecentServerInfo? recentInfo = userRecents.FirstOrDefault(recent => recent.ServerIp == server.Ip && recent.ServerPort == server.Port);

        _mapMap.TryGetValue(serverInfo.MapName, out string? mapDisplayName);
        _gameTypeMap.TryGetValue(serverInfo.GameType, out string? gameTypeDisplayName);

        ServerViewModel serverViewModel = new()
        {
            GameServerInfo = serverInfo,
            Ip = server.Ip,
            Port = server.Port,
            HostName = serverInfo.HostName,
            ClientNum = serverInfo.Clients - serverInfo.Bots,
            MaxClientNum = serverInfo.MaxClients,
            Game = serverInfo.GameName,
            GameType = serverInfo.GameType,
            GameTypeDisplayName = gameTypeDisplayName ?? serverInfo.GameType,
            Map = serverInfo.MapName,
            MapDisplayName = mapDisplayName ?? serverInfo.MapName,
            HasMap = _mapsProvider.InstalledMaps.Contains(serverInfo.MapName) || !_h2MLauncherOptions.Value.WatchGameDirectory,
            IsPrivate = serverInfo.IsPrivate,
            Ping = serverInfo.Ping,
            BotsNum = serverInfo.Bots,
            Protocol = serverInfo.Protocol,
            PrivilegedSlots = serverInfo.PrivilegedSlots,
            IsFavorite = isFavorite
        };

        // Game server responded -> online
        AllServersTab.Servers.Add(serverViewModel);

        if (isFavorite)
        {
            FavouritesTab.Servers.Add(serverViewModel);
        }

        if (recentInfo is not null)
        {
            serverViewModel.Joined = recentInfo.Joined;
            RecentsTab.Servers.Add(serverViewModel);
        }

        if (serverViewModel.Protocol == 3)
        {
            HMWServersTab.Servers.Add(serverViewModel);
        }
        else
        {
            H2MServersTab.Servers.Add(serverViewModel);
        }
    }

    private async Task JoinServer(ServerViewModel? serverViewModel)
    {
        string? password = null;

        if (serverViewModel is null)
            return;

        if (!CheckGameRunning())
        {
            return;
        }

        if (!serverViewModel.HasMap)
        {
            bool? dialogResult = _dialogService.OpenTextDialog(
                title: "Missing Map",
                text: """
                    You are trying to join a server with a map that's not installed. This might crash your game. 
                    Do you want to continue?
                    """,
                buttons: MessageBoxButton.YesNo);

            if (dialogResult == false)
            {
                return;
            }
        }

        if (serverViewModel.IsPrivate)
        {
            PasswordViewModel = new();

            bool? result = _dialogService.OpenDialog<PasswordDialog>(PasswordViewModel);

            password = PasswordViewModel.Password;

            // Do not continue joining the server
            if (result is null || result == false)
                return;
        }

        if (!_h2MLauncherOptions.CurrentValue.ServerQueueing)
        {
            // queueing disabled
            await JoinServerInternal(serverViewModel, password);
            return;
        }

        ServerData? serverData = _serverData.FirstOrDefault(d =>
            d.Ip == serverViewModel.Ip && d.Port == serverViewModel.Port);

        int privilegedSlots = serverViewModel.PrivilegedSlots < 0 ? serverData?.PrivilegedSlots ?? 0 : serverViewModel.PrivilegedSlots;
        int assumedMaxClients = serverViewModel.MaxClientNum - privilegedSlots;
        if (serverViewModel.ClientNum >= assumedMaxClients) //TODO: check if queueing enabled
        {
            // server is full (TODO: check again if refresh was long ago to avoid unnecessary server communication?)

            // Join the matchmaking server queue
            bool joinedQueue = await _matchmakingService.JoinQueueAsync(serverViewModel, password);

            if (joinedQueue)
            {
                MatchmakingViewModel queueViewModel = new(
                    _matchmakingService,
                    _serverDataService,
                    onForceJoin: (_) => JoinServerInternal(serverViewModel, password))
                {
                    ServerIp = serverViewModel.Ip,
                    ServerPort = serverViewModel.Port,
                    ServerHostName = serverViewModel.HostName,
                    CloseOnLeave = true
                };

                if (_dialogService.OpenDialog<QueueDialogView>(queueViewModel) == false)
                {
                    // queueing process terminated (left queue, joined, ...)
                    return;
                }
            }
            else if (_dialogService.OpenTextDialog("Queue unavailable", "Could not join the queue, force join instead?", MessageBoxButton.YesNo) == false)
            {
                return;
            }
        }

        await JoinServerInternal(serverViewModel, password);
    }

    private async Task<bool> JoinServerInternal(IServerConnectionDetails server, string? password)
    {
        bool hasJoined = await _h2MCommunicationService.JoinServer(server.Ip, server.Port.ToString(), password);
        if (hasJoined)
        {
            ServerViewModel? serverViewModel = server as ServerViewModel ?? FindServerViewModel(server);
            UpdateRecentJoinTime(serverViewModel, DateTime.Now);
            LastServer = server;
            _lastServerPassword = password?.ToSecuredString();
        }

        StatusText = hasJoined
            ? $"Joined {server.Ip}:{server.Port}"
            : "Ready";

        return hasJoined;
    }

    private Task ReconnectServer()
    {
        if (LastServer is not null)
        {
            return JoinServerInternal(LastServer, _lastServerPassword?.ToUnsecuredString());
        }

        return Task.CompletedTask;
    }

    private Task<bool> DisconnectServer()
    {
        return _h2MCommunicationService.Disconnect();
    }

    private bool CheckGameRunning()
    {
        if (_h2MCommunicationService.GameDetection.DetectedGame is not null)
        {
            return true;
        }
        bool? dialogResult = _dialogService.OpenTextDialog(
            title: "Game not running",
            text: "Matchmaking is only available when the game is running. Do you want to launch the game?",
            acceptButtonText: "Launch Game",
            cancelButtonText: "Cancel");

        if (dialogResult == true)
        {
            _h2MCommunicationService.LaunchH2MMod();
        }

        return false;
    }

    private async Task EnterMatchmaking()
    {
        if (!CheckGameRunning())
        {
            return;
        }

        MatchmakingViewModel matchmakingViewModel = new(
            _matchmakingService,
            _serverDataService,
            onForceJoin: (server) => JoinServerInternal(server, null));

        if (_dialogService.OpenDialog<QueueDialogView>(matchmakingViewModel) == false)
        {
            return;
        }

        await Task.CompletedTask;
    }

    private ServerViewModel? FindServerViewModel(IServerConnectionDetails server)
    {
        return AllServersTab.Servers.FirstOrDefault(s =>
                server.Ip == s.Ip && server.Port == s.Port);
    }

    private void MatchmakingService_Joined(ServerConnectionDetails joinedServer)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            ServerViewModel? serverViewModel = FindServerViewModel(joinedServer);
            if (serverViewModel is not null)
            {
                UpdateRecentJoinTime(serverViewModel, DateTime.Now);
                LastServer = serverViewModel;
            }
            else
            {
                LastServer = joinedServer;
            }
        });
    }

    private void LaunchH2M()
    {
        _h2MCommunicationService.LaunchH2MMod();
    }

    public void Dispose()
    {
        _h2MCommunicationService.GameDetection.GameDetected -= H2MCommunicationService_GameDetected;
        _h2MCommunicationService.GameDetection.GameExited -= H2MCommunicationService_GameExited;
        _h2MCommunicationService.GameDetection.Error -= GameDetection_Error;
        _h2MCommunicationService.GameCommunication.GameStateChanged -= H2MCommunicationService_GameStateChanged;
        _h2MCommunicationService.GameCommunication.Stopped -= H2MGameCommunication_Stopped;
        _matchmakingService.Joined -= MatchmakingService_Joined;
        _mapsProvider.MapsChanged -= MapsProvider_InstalledMapsChanged;
    }
}
