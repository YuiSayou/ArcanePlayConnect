using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ArcanePlayConnect.Core;
using ArcanePlayConnect.Core.Models;
using ArcanePlayConnect.Services;
using Windows.ApplicationModel.DataTransfer;

namespace ArcanePlayConnect.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static MainViewModel? _instance;
    public static MainViewModel Instance => _instance ??= new MainViewModel();

    private readonly LoggingService _logger;
    private readonly WebhookListenerService _webhookService;
    private readonly RconService _rconService;
    private readonly ProfileService _profileService;
    private readonly EventProcessor _eventProcessor;
    private readonly CommandButtonExecutor _buttonExecutor;
    private readonly CreatureTrackerService _creatureTracker;
    private readonly OverlayServerService _overlayServer;

    public ObservableCollection<LogEntry> FilteredLogs { get; } = new();
    public ObservableCollection<Profile> Profiles { get; } = new();
    public ObservableCollection<SavedCommand> SavedCommands { get; } = new();
    public ObservableCollection<CommandButton> CommandButtons { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartListenerCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopListenerCommand))]
    private bool _isListenerRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectRconCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectRconCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestCommandCommand))]
    private bool _isRconConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartListenerCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectRconCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    private Profile? _selectedProfile;

    [ObservableProperty]
    private string _webhookStatus = "Stopped";

    [ObservableProperty]
    private string _rconStatus = "Disconnected";

    [ObservableProperty]
    private string _tiktokStatus = "Simulated";

    [ObservableProperty]
    private string _editProfileName = string.Empty;

    [ObservableProperty]
    private string _editGameType = "Minecraft";

    [ObservableProperty]
    private int _editWebhookPort = 5000;

    [ObservableProperty]
    private string _editRconIP = "127.0.0.1";

    [ObservableProperty]
    private int _editRconPort = 25575;

    [ObservableProperty]
    private string _editRconPassword = string.Empty;

    [ObservableProperty]
    private string _testCommandText = "/say Hello from ArcanePlayConnect!";

    [ObservableProperty]
    private bool _showSystemLogs = true;

    [ObservableProperty]
    private bool _showChatLogs = true;

    [ObservableProperty]
    private bool _showFollowLogs = true;

    [ObservableProperty]
    private bool _showGiftLogs = true;

    [ObservableProperty]
    private bool _showLikeLogs = true;

    [ObservableProperty]
    private bool _showWebhookLogs = true;

    [ObservableProperty]
    private bool _isProfileEditorOpen;

    public ObservableCollection<ActionMappingItem> CurrentMappings { get; } = new();

    public CommandButtonExecutor ButtonExecutor => _buttonExecutor;
    public CreatureTrackerService CreatureTracker => _creatureTracker;
    public OverlayServerService OverlayServer => _overlayServer;

    public event Action? AddActionRequested;
    public event Action<SavedCommand>? SaveCommandRequested;

    private MainViewModel()
    {
        _logger = LoggingService.Instance;
        _webhookService = new WebhookListenerService(_logger);
        _rconService = new RconService(_logger);
        _profileService = new ProfileService(_logger);
        _buttonExecutor = new CommandButtonExecutor(_rconService, _logger);
        _creatureTracker = new CreatureTrackerService(_rconService, _logger);
        _overlayServer = new OverlayServerService(_logger, _creatureTracker);
        _eventProcessor = new EventProcessor(_rconService, _logger, _buttonExecutor, _creatureTracker);

        _webhookService.EventReceived += OnWebhookEvent;
        _webhookService.StatusChanged += OnWebhookStatusChanged;
        _rconService.ConnectionChanged += OnRconStatusChanged;

        _logger.Logs.CollectionChanged += OnRawLogsChanged;

        LoadProfiles();
        LoadSavedCommands();
        RestoreLastSelectedProfile();
        _logger.LogInfo("ArcanePlayConnect initialized.");
    }

    private void LoadProfiles()
    {
        Profiles.Clear();
        foreach (var p in _profileService.LoadAll())
        {
            Profiles.Add(p);
        }
    }

    private void LoadSavedCommands()
    {
        SavedCommands.Clear();
        foreach (var sc in _profileService.LoadSavedCommands())
            SavedCommands.Add(sc);
    }

    private void RestoreLastSelectedProfile()
    {
        var lastId = _profileService.LoadLastSelectedProfileId();
        if (!string.IsNullOrEmpty(lastId))
        {
            var match = Profiles.FirstOrDefault(p => p.Id == lastId);
            if (match != null)
            {
                SelectedProfile = match;
                return;
            }
        }
        if (Profiles.Count > 0)
        {
            SelectedProfile = Profiles[0];
        }
    }

    partial void OnSelectedProfileChanged(Profile? value)
    {
        if (value != null)
        {
            EditProfileName = value.ProfileName;
            EditGameType = value.GameType;
            EditWebhookPort = value.WebhookPort;
            EditRconIP = value.RconIP;
            EditRconPort = value.RconPort;
            EditRconPassword = value.RconPassword;
            RefreshMappings(value);
            RefreshCommandButtons(value);
            _profileService.SaveLastSelectedProfileId(value.Id);
        }
        else
        {
            IsProfileEditorOpen = false;
        }
    }

    partial void OnShowChatLogsChanged(bool value) => RebuildFilteredLogs();
    partial void OnShowFollowLogsChanged(bool value) => RebuildFilteredLogs();
    partial void OnShowGiftLogsChanged(bool value) => RebuildFilteredLogs();
    partial void OnShowLikeLogsChanged(bool value) => RebuildFilteredLogs();
    partial void OnShowWebhookLogsChanged(bool value) => RebuildFilteredLogs();
    partial void OnShowSystemLogsChanged(bool value) => RebuildFilteredLogs();

    private void OnRawLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            foreach (LogEntry entry in e.NewItems)
            {
                if (ShouldShowEntry(entry))
                    FilteredLogs.Add(entry);
            }
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            RebuildFilteredLogs();
        }
    }

    private void RebuildFilteredLogs()
    {
        FilteredLogs.Clear();
        foreach (var entry in _logger.Logs)
        {
            if (ShouldShowEntry(entry))
                FilteredLogs.Add(entry);
        }
    }

    private bool ShouldShowEntry(LogEntry entry)
    {
        return entry.Category switch
        {
            LogCategory.Chat    => ShowChatLogs,
            LogCategory.Follow  => ShowFollowLogs,
            LogCategory.Gift    => ShowGiftLogs,
            LogCategory.Like    => ShowLikeLogs,
            LogCategory.Webhook => ShowWebhookLogs,
            LogCategory.System  => ShowSystemLogs,
            _                   => ShowSystemLogs
        };
    }

    private void RefreshMappings(Profile profile)
    {
        CurrentMappings.Clear();
        foreach (var mapping in profile.ActionMappings)
        {
            var btnName = string.Empty;
            if (!string.IsNullOrEmpty(mapping.TargetButtonId))
            {
                var btn = profile.CommandButtons.FirstOrDefault(b => b.Id == mapping.TargetButtonId);
                btnName = btn?.Name ?? string.Empty;
            }

            CurrentMappings.Add(new ActionMappingItem
            {
                TriggerType      = mapping.TriggerType,
                TriggerKey       = mapping.TriggerKey,
                Command          = mapping.Command,
                TargetButtonId   = mapping.TargetButtonId,
                TargetButtonName = btnName
            });
        }
    }

    private void RefreshCommandButtons(Profile profile)
    {
        _buttonExecutor.StopAll();
        CommandButtons.Clear();
        foreach (var btn in profile.CommandButtons)
            CommandButtons.Add(btn);
    }

    // ?? Profile CRUD ????????????????????????????????????????????????????????

    [RelayCommand]
    private void CreateProfile()
    {
        var profile = new Profile { ProfileName = "New Profile" };
        _profileService.Save(profile);
        Profiles.Add(profile);
        SelectedProfile = profile;
        IsProfileEditorOpen = true;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProfile))]
    private void SaveProfile()
    {
        if (SelectedProfile == null) return;

        SelectedProfile.ProfileName = EditProfileName;
        SelectedProfile.GameType = EditGameType;
        SelectedProfile.WebhookPort = EditWebhookPort;
        SelectedProfile.RconIP = EditRconIP;
        SelectedProfile.RconPort = EditRconPort;
        SelectedProfile.RconPassword = EditRconPassword;

        SelectedProfile.ActionMappings.Clear();
        foreach (var item in CurrentMappings)
        {
            if (!string.IsNullOrWhiteSpace(item.Command) || !string.IsNullOrWhiteSpace(item.TargetButtonId))
            {
                SelectedProfile.ActionMappings.Add(new ActionMapping
                {
                    TriggerType    = item.TriggerType,
                    TriggerKey     = item.TriggerKey,
                    Command        = item.Command,
                    TargetButtonId = item.TargetButtonId
                });
            }
        }

        _profileService.Save(SelectedProfile);

        var idx = Profiles.IndexOf(SelectedProfile);
        if (idx >= 0)
        {
            var p = SelectedProfile;
            Profiles.RemoveAt(idx);
            Profiles.Insert(idx, p);
            SelectedProfile = p;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProfile))]
    private void DeleteProfile()
    {
        if (SelectedProfile == null) return;
        _profileService.Delete(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.Count > 0 ? Profiles[0] : null;
        if (SelectedProfile == null) IsProfileEditorOpen = false;
    }

    [RelayCommand]
    private void EditSelectedProfile()
    {
        if (SelectedProfile != null)
            IsProfileEditorOpen = true;
    }

    private bool HasSelectedProfile() => SelectedProfile != null;

    // ?? Action Mappings ?????????????????????????????????????????????????????

    [RelayCommand]
    private void OpenAddActionDialog()
    {
        AddActionRequested?.Invoke();
    }

    public void AddActionMapping(ActionMappingItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Command) && string.IsNullOrWhiteSpace(item.TargetButtonId)) return;
        CurrentMappings.Add(item);
        SyncAndSaveMappings();
    }

    public void RemoveActionMapping(ActionMappingItem item)
    {
        if (CurrentMappings.Remove(item))
            SyncAndSaveMappings();
    }

    private void SyncAndSaveMappings()
    {
        if (SelectedProfile == null) return;

        SelectedProfile.ActionMappings.Clear();
        foreach (var item in CurrentMappings)
        {
            if (!string.IsNullOrWhiteSpace(item.Command) || !string.IsNullOrWhiteSpace(item.TargetButtonId))
            {
                SelectedProfile.ActionMappings.Add(new ActionMapping
                {
                    TriggerType    = item.TriggerType,
                    TriggerKey     = item.TriggerKey,
                    Command        = item.Command,
                    TargetButtonId = item.TargetButtonId
                });
            }
        }

        _profileService.Save(SelectedProfile);
    }

    [RelayCommand]
    private async Task TestMapping(ActionMappingItem? item)
    {
        if (item == null) return;

        if (!_rconService.IsConnected)
        {
            _logger.LogWarning("RCON not connected. Cannot test mapping.");
            return;
        }

        if (!string.IsNullOrEmpty(item.TargetButtonId))
        {
            var button = CommandButtons.FirstOrDefault(b => b.Id == item.TargetButtonId);
            if (button != null)
            {
                _logger.LogInfo($"[TEST] {item.TriggerDisplay} \u2192 Button '{button.Name}'", LogCategory.System);
                await ExecuteCommandButton(button, "TestPlayer", "testplayer");
            }
            else
            {
                _logger.LogWarning($"Button not found: {item.TargetButtonId}");
            }
        }
        else
        {
            var command = EventProcessor.BuildCommand(item.Command, "TestPlayer", "testplayer");
            _logger.LogInfo($"[TEST] {item.TriggerDisplay} \u2192 {command}", LogCategory.System);
            await _rconService.SendCommand(command);
        }
    }

    // ?? Command Buttons ?????????????????????????????????????????????????????

    public void AddCommandButton(CommandButton button)
    {
        CommandButtons.Add(button);
        SyncAndSaveButtons();
    }

    public void UpdateCommandButton(CommandButton button)
    {
        SyncAndSaveButtons();
    }

    public void RemoveCommandButton(CommandButton button)
    {
        // Stop if running
        if (_buttonExecutor.IsRunning(button.Id))
            _buttonExecutor.ToggleContinuous(button);

        CommandButtons.Remove(button);

        // Remove any mappings that reference this button
        var toRemove = CurrentMappings.Where(m => m.TargetButtonId == button.Id).ToList();
        foreach (var m in toRemove) CurrentMappings.Remove(m);

        SyncAndSaveButtons();
        if (toRemove.Count > 0) SyncAndSaveMappings();
    }

    public async Task ExecuteCommandButton(CommandButton button, string nickname = "", string username = "")
    {
        // For Summon-type buttons with structured settings, route through creature tracker
        if (button.ButtonType == CommandButtonType.Summon &&
            !string.IsNullOrEmpty(button.SummonEntityType))
        {
            await ExecuteSummonButtonAsync(button, nickname, username);
            return;
        }

        // All other buttons go through the standard executor
        await _buttonExecutor.ExecuteAsync(button, nickname, username);
    }

    /// <summary>
    /// Executes a Summon button: summons the creature via tracker, then runs additional commands.
    /// </summary>
    private async Task ExecuteSummonButtonAsync(CommandButton button, string nickname, string username)
    {
        if (!_rconService.IsConnected)
        {
            _logger.LogWarning("RCON not connected. Cannot execute summon button.", LogCategory.System);
            return;
        }

        // Use defaults for manual Execute clicks when no viewer info is available
        var nick = string.IsNullOrWhiteSpace(nickname) ? "TestPlayer" : nickname;
        var user = string.IsNullOrWhiteSpace(username) ? "testplayer" : username;

        // Summon the creature via tracker with custom HP/attack
        var creature = await _creatureTracker.SummonCreatureAsync(
            nick,
            user,
            button.SummonEntityType,
            button.SummonPosition,
            extraNbt: "",
            customHealth: button.SummonCustomHealth,
            customAttackDamage: button.SummonCustomAttack,
            isBoss: button.SummonIsBoss,
            bossName: button.SummonBossName);

        if (creature == null) return; // blocked or failed

        // Execute any additional commands in the button
        foreach (var template in button.Commands)
        {
            if (string.IsNullOrWhiteSpace(template)) continue;

            var cmd = template;
            if (button.UseNickname)
                cmd = EventProcessor.BuildCommand(template, nick, user);

            // Replace {tag} with the creature's tracking tag for command chaining
            cmd = cmd.Replace("{tag}", creature.TrackingId);

            await _rconService.SendCommand(cmd);
        }
    }

    private void SyncAndSaveButtons()
    {
        if (SelectedProfile == null) return;

        SelectedProfile.CommandButtons.Clear();
        foreach (var btn in CommandButtons)
            SelectedProfile.CommandButtons.Add(btn);

        _profileService.Save(SelectedProfile);
    }

    // ?? Saved Commands ??????????????????????????????????????????????????????

    public void AddSavedCommand(SavedCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Label) || string.IsNullOrWhiteSpace(cmd.Command))
            return;

        var existing = SavedCommands.FirstOrDefault(c =>
            string.Equals(c.Label, cmd.Label, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            existing.Command = cmd.Command;
        else
            SavedCommands.Add(cmd);

        _profileService.SaveSavedCommands(SavedCommands);
        _logger.LogInfo($"Command '{cmd.Label}' saved to library.");
    }

    public void DeleteSavedCommand(SavedCommand cmd)
    {
        if (SavedCommands.Remove(cmd))
        {
            _profileService.SaveSavedCommands(SavedCommands);
            _logger.LogInfo($"Command '{cmd.Label}' removed from library.");
        }
    }

    // ?? Logs ????????????????????????????????????????????????????????????????

    [RelayCommand]
    private void CopyAllLogs()
    {
        if (FilteredLogs.Count == 0)
        {
            _logger.LogWarning("No log entries to copy.");
            return;
        }

        var sb = new StringBuilder();
        foreach (var entry in FilteredLogs)
            sb.AppendLine($"[{entry.FormattedTime}] [{entry.Level}] [{entry.Category}] {entry.Message}");

        var dp = new DataPackage();
        dp.SetText(sb.ToString());
        Clipboard.SetContent(dp);
        Clipboard.Flush();
        _logger.LogInfo("Log entries copied to clipboard.");
    }

    [RelayCommand]
    private void CopySelectedLog(LogEntry? entry)
    {
        if (entry == null) return;
        var text = $"[{entry.FormattedTime}] [{entry.Level}] [{entry.Category}] {entry.Message}";
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
        Clipboard.Flush();
    }

    [RelayCommand]
    private void ClearLogs()
    {
        _logger.Logs.Clear();
        FilteredLogs.Clear();
    }

    // ?? Connection ??????????????????????????????????????????????????????????

    [RelayCommand(CanExecute = nameof(CanStartListener))]
    private void StartListener()
    {
        if (SelectedProfile == null)
        {
            _logger.LogWarning("Create and select a profile before starting the listener.");
            return;
        }
        _webhookService.Start(SelectedProfile.WebhookPort);
    }

    private bool CanStartListener() => !IsListenerRunning && SelectedProfile != null;

    [RelayCommand(CanExecute = nameof(CanStopListener))]
    private void StopListener()
    {
        _webhookService.Stop();
    }

    private bool CanStopListener() => IsListenerRunning;

    [RelayCommand(CanExecute = nameof(CanConnectRcon))]
    private async Task ConnectRcon()
    {
        if (SelectedProfile == null)
        {
            _logger.LogWarning("Select a profile first.");
            return;
        }
        await _rconService.ConnectAsync(
            SelectedProfile.RconIP,
            SelectedProfile.RconPort,
            SelectedProfile.RconPassword);
    }

    private bool CanConnectRcon() => !IsRconConnected && SelectedProfile != null;

    [RelayCommand(CanExecute = nameof(CanDisconnectRcon))]
    private void DisconnectRcon()
    {
        _rconService.Disconnect();
    }

    private bool CanDisconnectRcon() => IsRconConnected;

    [RelayCommand(CanExecute = nameof(CanTestCommand))]
    private async Task TestCommand()
    {
        if (!string.IsNullOrWhiteSpace(TestCommandText))
        {
            await _rconService.SendCommand(TestCommandText);
            _logger.LogInfo($"Test command sent: {TestCommandText}");
        }
    }

    private bool CanTestCommand() => IsRconConnected;

    private void OnWebhookEvent(WebhookEvent evt)
    {
        _ = _eventProcessor.ProcessEvent(evt, SelectedProfile);
    }

    private void OnWebhookStatusChanged()
    {
        IsListenerRunning = _webhookService.IsRunning;
        WebhookStatus = IsListenerRunning ? "Running" : "Stopped";
    }

    private void OnRconStatusChanged()
    {
        IsRconConnected = _rconService.IsConnected;
        RconStatus = IsRconConnected ? "Connected" : "Disconnected";
    }
}

public class ActionMappingItem
{
    public ActionTriggerType TriggerType { get; set; } = ActionTriggerType.Gift;
    public string TriggerKey { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string TargetButtonId { get; set; } = string.Empty;
    public string TargetButtonName { get; set; } = string.Empty;

    public string TriggerTypeLabel => TriggerType switch
    {
        ActionTriggerType.Gift   => "\uE8E1",
        ActionTriggerType.Follow => "\uE77B",
        ActionTriggerType.Chat   => "\uE8BD",
        ActionTriggerType.Like   => "\uEB51",
        _ => "?"
    };

    public string TriggerDisplay => TriggerType switch
    {
        ActionTriggerType.Follow => "Any Follow",
        ActionTriggerType.Chat   when string.IsNullOrWhiteSpace(TriggerKey) => "Any Chat",
        ActionTriggerType.Like   when string.IsNullOrWhiteSpace(TriggerKey) => "Any Like",
        ActionTriggerType.Like   => $"\u2265{TriggerKey} likes",
        _ => TriggerKey
    };

    public bool IsButtonTrigger => !string.IsNullOrEmpty(TargetButtonId);

    public string ActionDisplay => IsButtonTrigger
        ? (string.IsNullOrEmpty(TargetButtonName) ? $"Button ({TargetButtonId[..Math.Min(8, TargetButtonId.Length)]}...)" : TargetButtonName)
        : Command;

    public string ActionGlyph => IsButtonTrigger ? "\uE946" : "\uE756";

    public Microsoft.UI.Xaml.Media.SolidColorBrush ActionColor => IsButtonTrigger
        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 149, 0))
        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 255, 136));
}
