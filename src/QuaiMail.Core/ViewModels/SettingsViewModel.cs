using CommunityToolkit.Mvvm.ComponentModel;
using QuaiMail.Core.Agent;
using QuaiMail.Core.Models;
using QuaiMail.Core.Storage;

namespace QuaiMail.Core.ViewModels;

/// <summary>Réglages : chaque modification est appliquée et enregistrée immédiatement.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IBackgroundAgent? _agent;
    private bool _loading;

    public SettingsViewModel(ISettingsService settings, AppPaths paths, IBackgroundAgent? agent = null)
    {
        _settings = settings;
        _agent = agent;
        AgentAvailable = agent?.IsAvailable == true;
        DataDirectory = paths.Root;
        LogsDirectory = paths.LogsDirectory;
        Load();
        _settings.Changed += (_, _) => Load();
    }

    public string Version => AppInfo.Version;

    public string DataDirectory { get; }

    public string LogsDirectory { get; }

    /// <summary>Index dans <see cref="AppTheme"/> : 0 = système, 1 = clair, 2 = sombre.</summary>
    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    public partial bool BlockRemoteImages { get; set; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    public partial bool ShowNotifications { get; set; }

    /// <summary>Pastille des non lus sur l'icône de la barre des tâches.</summary>
    [ObservableProperty]
    public partial bool ShowUnreadBadge { get; set; }

    /// <summary><c>QuaiMailAgent.exe</c> est livré avec cette copie de Quai Mail (sinon, ses réglages sont cachés).</summary>
    public bool AgentAvailable { get; }

    /// <summary>Agent de notification : nouveaux mails annoncés quand Quai Mail est fermé.</summary>
    [ObservableProperty]
    public partial bool BackgroundAgent { get; set; }

    /// <summary>L'agent se lance à l'ouverture de la session Windows (lu dans le registre, pas dans settings.json).</summary>
    [ObservableProperty]
    public partial bool AgentAutoStart { get; set; }

    partial void OnBackgroundAgentChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        _settings.Update(s => s.BackgroundAgent = value);
        if (!value && _agent is not null)
        {
            // Coupé : l'agent s'arrête et ne se lance plus avec Windows. Réactivé : il reprend à la fermeture de Quai Mail.
            _agent.Stop();
            SetAutoStart(false);
        }
    }

    partial void OnAgentAutoStartChanged(bool value)
    {
        if (!_loading)
        {
            SetAutoStart(value);
        }
    }

    private void SetAutoStart(bool enabled)
    {
        if (_agent is null)
        {
            return;
        }

        _agent.SetAutoStart(enabled);
        _loading = true;
        try
        {
            AgentAutoStart = _agent.IsAutoStartEnabled;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Clé inconnue d'un mail signé demandée d'office au serveur de clés.</summary>
    [ObservableProperty]
    public partial bool LookUpPgpKeys { get; set; }

    partial void OnLookUpPgpKeysChanged(bool value)
    {
        if (!_loading)
        {
            _settings.Update(s => s.LookUpPgpKeys = value);
        }
    }

    partial void OnShowNotificationsChanged(bool value)
    {
        if (!_loading)
        {
            _settings.Update(s => s.ShowNotifications = value);
        }
    }

    partial void OnShowUnreadBadgeChanged(bool value)
    {
        if (!_loading)
        {
            _settings.Update(s => s.ShowUnreadBadge = value);
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (!_loading && Enum.IsDefined((AppTheme)value))
        {
            _settings.Update(s => s.Theme = (AppTheme)value);
        }
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (!_loading)
        {
            _settings.Update(s => s.CheckForUpdates = value);
        }
    }

    partial void OnBlockRemoteImagesChanged(bool value)
    {
        if (!_loading)
        {
            _settings.Update(s => s.BlockRemoteImages = value);
        }
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var current = _settings.Current;
            ThemeIndex = (int)current.Theme;
            BlockRemoteImages = current.BlockRemoteImages;
            CheckForUpdates = current.CheckForUpdates;
            ShowNotifications = current.ShowNotifications;
            ShowUnreadBadge = current.ShowUnreadBadge;
            LookUpPgpKeys = current.LookUpPgpKeys;
            BackgroundAgent = current.BackgroundAgent;
            AgentAutoStart = _agent?.IsAutoStartEnabled == true;
        }
        finally
        {
            _loading = false;
        }
    }
}
