using System.Runtime.Versioning;
using Microsoft.Win32;
using QuaiMail.Core.Agent;

namespace QuaiMail.Core.Tests.Agent;

/// <summary>Lancement avec Windows, sur des clés de test (jamais la vraie clé Run de l'utilisateur). Windows seulement.</summary>
[SupportedOSPlatform("windows")]
public sealed class AutoStartTests : IDisposable
{
    private readonly string _root = $@"Software\QuaiMailTests\{Guid.NewGuid():N}";

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Enabling_then_disabling_writes_and_removes_the_run_value()
    {
        var autoStart = Create();
        Assert.False(autoStart.IsEnabled);
        Assert.False(autoStart.WasChosen);

        autoStart.Enable(@"C:\Users\léa\AppData\Local\Programs\Quai Mail\QuaiMailAgent.exe");
        Assert.True(autoStart.IsEnabled);
        Assert.Equal(@"C:\Users\léa\AppData\Local\Programs\Quai Mail\QuaiMailAgent.exe", autoStart.RegisteredPath);
        using (var run = Registry.CurrentUser.OpenSubKey(_root + @"\Run"))
        {
            // Entre guillemets : le chemin contient des espaces.
            Assert.Equal("\"C:\\Users\\léa\\AppData\\Local\\Programs\\Quai Mail\\QuaiMailAgent.exe\"", run!.GetValue("QuaiMailAgent"));
        }

        autoStart.Disable();
        Assert.False(autoStart.IsEnabled);
        Assert.Null(autoStart.RegisteredPath);
        Assert.True(autoStart.WasChosen);
        autoStart.Disable();
    }

    [Fact]
    public void A_start_disabled_in_the_task_manager_is_respected_and_enabling_again_restores_it()
    {
        var autoStart = Create();
        autoStart.Enable(@"C:\Quai Mail\QuaiMailAgent.exe");
        using (var approved = Registry.CurrentUser.CreateSubKey(_root + @"\Approved"))
        {
            approved.SetValue("QuaiMailAgent", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }

        Assert.False(autoStart.IsEnabled);

        autoStart.Enable(@"C:\Quai Mail\QuaiMailAgent.exe");
        Assert.True(autoStart.IsEnabled);
        using (var approved = Registry.CurrentUser.OpenSubKey(_root + @"\Approved"))
        {
            Assert.Null(approved!.GetValue("QuaiMailAgent"));
        }
    }

    [Fact]
    public void An_approval_marked_enabled_counts_as_enabled()
    {
        var autoStart = Create();
        autoStart.Enable(@"C:\Quai Mail\QuaiMailAgent.exe");
        using (var approved = Registry.CurrentUser.CreateSubKey(_root + @"\Approved"))
        {
            approved.SetValue("QuaiMailAgent", new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }

        Assert.True(autoStart.IsEnabled);
    }

    private AutoStart Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Registre de Windows : test lancé seulement sous Windows (CI).");
        }

        return new AutoStart(Registry.CurrentUser, _root + @"\Run", _root + @"\Approved", _root + @"\Choice", "QuaiMailAgent");
    }
}
