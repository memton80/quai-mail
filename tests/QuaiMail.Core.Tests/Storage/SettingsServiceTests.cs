using QuaiMail.Core.Models;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Tests.Support;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.Core.Tests.Storage;

public sealed class SettingsServiceTests
{
    [Fact]
    public void Defaults_apply_when_there_is_no_file()
    {
        using var temp = new TempDirectory();

        var settings = new JsonSettingsService(temp.File("settings.json")).Current;

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.True(settings.BlockRemoteImages);
        Assert.True(settings.ShowNotifications);
        Assert.True(settings.ShowUnreadBadge);
    }

    [Fact]
    public void Changes_are_written_in_camel_case_and_read_back()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");

        new JsonSettingsService(path).Update(s =>
        {
            s.Theme = AppTheme.Dark;
            s.BlockRemoteImages = false;
        });

        Assert.Contains("\"theme\": \"dark\"", File.ReadAllText(path));
        var reloaded = new JsonSettingsService(path).Current;
        Assert.Equal(AppTheme.Dark, reloaded.Theme);
        Assert.False(reloaded.BlockRemoteImages);
    }

    [Fact]
    public void An_unreadable_file_is_set_aside_and_defaults_apply()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, "{ pas du json");

        var settings = new JsonSettingsService(path).Current;

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Single(Directory.GetFiles(temp.Path, "settings.json.corrompu-*"));
    }

    [Fact]
    public void An_unknown_theme_falls_back_to_system()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, """{ "theme": 42 }""");

        Assert.Equal(AppTheme.System, new JsonSettingsService(path).Current.Theme);
    }

    [Fact]
    public void Changed_is_raised_only_for_real_changes()
    {
        using var temp = new TempDirectory();
        var service = new JsonSettingsService(temp.File("settings.json"));
        var raised = 0;
        service.Changed += (_, _) => raised++;

        service.Update(s => s.Theme = AppTheme.System);
        service.Update(s => s.Theme = AppTheme.Light);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Collapsed_accounts_are_kept_without_duplicates_or_blanks()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, """{ "collapsedAccounts": ["b", "", "a", "b"] }""");

        Assert.Equal(["b", "a"], new JsonSettingsService(path).Current.CollapsedAccounts);

        var service = new JsonSettingsService(path);
        var raised = 0;
        service.Changed += (_, _) => raised++;
        service.Update(s => s.CollapsedAccounts = ["b", "a"]);
        service.Update(s => s.CollapsedAccounts = ["a"]);

        Assert.Equal(1, raised);
        Assert.Equal(["a"], new JsonSettingsService(path).Current.CollapsedAccounts);
    }

    [Fact]
    public void The_pane_is_expanded_by_default_and_its_compact_choice_is_saved()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        var service = new JsonSettingsService(path);
        Assert.False(service.Current.NavPaneCompact);

        service.Update(s => s.NavPaneCompact = true);

        Assert.True(new JsonSettingsService(path).Current.NavPaneCompact);
    }

    [Fact]
    public void A_null_list_of_collapsed_accounts_means_none()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, """{ "collapsedAccounts": null }""");

        Assert.Empty(new JsonSettingsService(path).Current.CollapsedAccounts);
    }

    [Fact]
    public void SettingsViewModel_saves_each_change()
    {
        using var temp = new TempDirectory();
        var service = new JsonSettingsService(temp.File("settings.json"));
        var viewModel = new SettingsViewModel(service, new AppPaths(temp.Path));

        viewModel.ThemeIndex = 2;
        viewModel.BlockRemoteImages = false;
        viewModel.LookUpPgpKeys = false;

        Assert.Equal(AppTheme.Dark, service.Current.Theme);
        Assert.False(service.Current.BlockRemoteImages);
        Assert.False(service.Current.LookUpPgpKeys);
    }

    /// <summary>Garde-fou : chaque interrupteur, une fois changé, est vraiment enregistré (aucun oublié dans la comparaison).</summary>
    [Fact]
    public void Every_switch_is_saved_when_changed()
    {
        foreach (var property in typeof(AppSettings).GetProperties().Where(p => p.PropertyType == typeof(bool) && p.CanWrite))
        {
            using var temp = new TempDirectory();
            var path = temp.File("settings.json");
            var service = new JsonSettingsService(path);
            var before = (bool)property.GetValue(service.Current)!;
            var changed = 0;
            service.Changed += (_, _) => changed++;

            service.Update(s => property.SetValue(s, !before));

            Assert.True(changed == 1, property.Name + " : changement ignoré");
            Assert.True((bool)property.GetValue(new JsonSettingsService(path).Current)! != before, property.Name + " : non enregistré");
        }
    }

    [Fact]
    public void AtomicFile_replaces_content_without_leaving_temporary_files()
    {
        using var temp = new TempDirectory();
        var path = temp.File("doc.pdf");
        File.WriteAllBytes(path, [1, 2, 3]);

        AtomicFile.Write(path, [4, 5]);

        Assert.Equal([4, 5], File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(temp.Path));
    }
}
