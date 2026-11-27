using System.Runtime.Versioning;
using Microsoft.Win32;

namespace QuaiMail.Core.Agent;

/// <summary>
/// Lancement de l'agent à l'ouverture de la session Windows : valeur <c>QuaiMailAgent</c> de
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> (par utilisateur, sans droits administrateur), comme la
/// plupart des applications de la zone de notification. Windows permet aussi de le couper dans le Gestionnaire des
/// tâches (onglet « Applications de démarrage », clé <c>StartupApproved</c>) : ce choix est respecté et lu ici.
/// Chaque réglage (Quai Mail, menu de l'icône) est noté dans <c>HKCU\Software\Quai Mail</c> (<see cref="WasChosen"/>) :
/// le lancement avec Windows n'est activé d'office qu'une fois, un choix de l'utilisateur n'est jamais défait.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutoStart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>Clé de Quai Mail (créée par l'installateur, effacée à la désinstallation).</summary>
    public const string ChoiceKey = @"Software\Quai Mail";

    private const string ChoiceValue = "AgentAutoStartChosen";

    private readonly RegistryKey _root;
    private readonly string _runKey;
    private readonly string _approvedKey;
    private readonly string _choiceKey;
    private readonly string _valueName;

    public AutoStart()
        : this(Registry.CurrentUser, RunKey, ApprovedKey, ChoiceKey, AgentInfo.RunValueName)
    {
    }

    /// <summary>Tests : clés à part, jamais celles de l'utilisateur.</summary>
    public AutoStart(RegistryKey root, string runKey, string approvedKey, string choiceKey, string valueName)
    {
        _root = root;
        _runKey = runKey;
        _approvedKey = approvedKey;
        _choiceKey = choiceKey;
        _valueName = valueName;
    }

    /// <summary>Le lancement avec Windows a déjà été réglé une fois (d'office ou par l'utilisateur).</summary>
    public bool WasChosen
    {
        get
        {
            using var key = _root.OpenSubKey(_choiceKey);
            return key?.GetValue(ChoiceValue) is int and not 0;
        }
    }

    /// <summary>L'agent se lance avec Windows : valeur présente et non désactivée dans le Gestionnaire des tâches.</summary>
    public bool IsEnabled
    {
        get
        {
            using var run = _root.OpenSubKey(_runKey);
            if (run?.GetValue(_valueName) is not string { Length: > 0 })
            {
                return false;
            }

            // StartupApproved : 12 octets, le premier pair = activé (02, 06), impair = désactivé (03, 07). Absent = activé.
            using var approved = _root.OpenSubKey(_approvedKey);
            return approved?.GetValue(_valueName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }
    }

    /// <summary>Chemin enregistré (tests, diagnostic), sans les guillemets.</summary>
    public string? RegisteredPath
    {
        get
        {
            using var run = _root.OpenSubKey(_runKey);
            return (run?.GetValue(_valueName) as string)?.Trim('"');
        }
    }

    /// <summary>Lance l'agent avec Windows (réactive aussi un démarrage coupé dans le Gestionnaire des tâches).</summary>
    public void Enable(string agentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentPath);
        using (var run = _root.CreateSubKey(_runKey, writable: true))
        {
            run.SetValue(_valueName, $"\"{agentPath}\"", RegistryValueKind.String);
        }

        RemoveApproval();
        RememberChoice();
    }

    /// <summary>Ne lance plus l'agent avec Windows (sans effet s'il ne l'était pas).</summary>
    public void Disable()
    {
        using (var run = _root.OpenSubKey(_runKey, writable: true))
        {
            run?.DeleteValue(_valueName, throwOnMissingValue: false);
        }

        RemoveApproval();
        RememberChoice();
    }

    private void RememberChoice()
    {
        using var key = _root.CreateSubKey(_choiceKey, writable: true);
        key.SetValue(ChoiceValue, 1, RegistryValueKind.DWord);
    }

    private void RemoveApproval()
    {
        using var approved = _root.OpenSubKey(_approvedKey, writable: true);
        approved?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
