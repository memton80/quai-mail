using CommunityToolkit.Mvvm.ComponentModel;
using QuaiMail.Core.Models;
using QuaiMail.Core.Storage;

namespace QuaiMail.Core.ViewModels;

/// <summary>Réglages : chaque modification est appliquée et enregistrée immédiatement.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private bool _loading;

    public SettingsViewModel(ISettingsService settings, AppPaths paths)
    {
        _settings = settings;
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
            LookUpPgpKeys = current.LookUpPgpKeys;
        }
        finally
        {
            _loading = false;
        }
    }
}
