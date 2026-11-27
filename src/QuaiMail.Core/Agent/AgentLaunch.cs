namespace QuaiMail.Core.Agent;

/// <summary>
/// Clic sur une notification de l'agent : <c>QuaiMail.exe --inbox &lt;compte&gt;</c> ouvre Quai Mail sur la boîte de
/// réception de ce compte. Seul un identifiant interne de compte est accepté (32 chiffres hexadécimaux, jamais une adresse).
/// </summary>
public static class AgentLaunch
{
    public const string InboxSwitch = "--inbox";

    /// <summary>Arguments à passer à Quai Mail pour ouvrir la boîte de réception de ce compte.</summary>
    public static string InboxArguments(string accountId) =>
        IsAccountId(accountId) ? $"{InboxSwitch} {accountId}" : throw new ArgumentException("Identifiant de compte invalide.", nameof(accountId));

    /// <summary>
    /// Cherche <c>--inbox &lt;id&gt;</c> dans des arguments, découpés (ligne de commande) ou non (lancement redirigé vers
    /// la fenêtre déjà ouverte, qui reçoit une seule chaîne).
    /// </summary>
    public static bool TryGetInbox(IEnumerable<string?> arguments, out string accountId)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var words = arguments
            .SelectMany(a => (a ?? string.Empty).Split([' ', '\t', '"'], StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        for (var i = 0; i < words.Count - 1; i++)
        {
            if (string.Equals(words[i], InboxSwitch, StringComparison.OrdinalIgnoreCase) && IsAccountId(words[i + 1]))
            {
                accountId = words[i + 1];
                return true;
            }
        }

        accountId = string.Empty;
        return false;
    }

    /// <summary>Format des identifiants de <see cref="Accounts.Account.Id"/> (<c>Guid.ToString("N")</c>).</summary>
    public static bool IsAccountId(string? value) =>
        value is { Length: 32 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
