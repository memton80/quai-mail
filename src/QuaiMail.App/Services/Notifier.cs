using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using QuaiMail.App.Localization;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Mail;

namespace QuaiMail.App.Services;

/// <summary>Notifications de Windows à l'arrivée de mails.</summary>
internal interface INotifier
{
    /// <summary>Un clic sur une notification : le compte concerné (sa boîte de réception est affichée).</summary>
    event EventHandler<string>? Invoked;

    void Show(Account account, IReadOnlyList<MessageHeader> messages);
}

/// <summary>
/// Notifications du Windows App SDK (<c>AppNotificationManager</c>, application non packagée). Elles ne contiennent
/// que l'expéditeur et l'objet, comme toute messagerie ; rien n'est journalisé. Désactivables dans les réglages.
/// </summary>
internal sealed class AppNotifier : INotifier
{
    private readonly ILogger _logger;
    private bool _registered;

    public AppNotifier(ILogger<AppNotifier> logger)
    {
        _logger = logger;
    }

    public event EventHandler<string>? Invoked;

    /// <summary>Inscription auprès de Windows (au démarrage) ; un échec laisse simplement l'application sans notifications.</summary>
    public void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, args) =>
            {
                if (args.Arguments.TryGetValue("account", out var account))
                {
                    Invoked?.Invoke(this, account);
                }
            };
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Notifications indisponibles ({Error})", ex.GetType().Name);
        }
    }

    public void Show(Account account, IReadOnlyList<MessageHeader> messages)
    {
        if (!_registered || messages.Count == 0)
        {
            return;
        }

        try
        {
            var first = messages[0];
            var builder = new AppNotificationBuilder().AddArgument("account", account.Id);
            if (messages.Count == 1)
            {
                builder.AddText(first.Sender).AddText(string.IsNullOrWhiteSpace(first.Subject) ? Loc.Get("Draft_NoSubject") : first.Subject);
            }
            else
            {
                builder.AddText(Loc.Format("Notification_Several", messages.Count, account.Title)).AddText(Loc.Format("Notification_Latest", first.Sender));
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Notification non affichée ({Error})", ex.GetType().Name);
        }
    }

    /// <summary>Désinstallation : Quai Mail est retiré des notifications de Windows.</summary>
    public static void UnregisterAll()
    {
        try
        {
            AppNotificationManager.Default.UnregisterAll();
        }
        catch (Exception)
        {
            // Rien d'inscrit.
        }
    }
}

/// <summary>Test de fumée : les notifications sont notées, jamais affichées.</summary>
internal sealed class SmokeNotifier : INotifier
{
    public event EventHandler<string>? Invoked;

    public List<(string AccountId, int Count)> Shown { get; } = [];

    public void Show(Account account, IReadOnlyList<MessageHeader> messages) => Shown.Add((account.Id, messages.Count));

    public void Click(string accountId) => Invoked?.Invoke(this, accountId);
}

/// <summary>Test de fumée : réception en direct simulée (aucune connexion).</summary>
internal sealed class SmokeMailWatcher : IMailWatcher
{
    public event EventHandler<NewMailEventArgs>? NewMail;

    public IReadOnlyList<Account> Watched { get; private set; } = [];

    public void Watch(IReadOnlyList<Account> accounts) => Watched = accounts;

    public void Deliver(Account account, IReadOnlyList<MessageHeader> messages) => NewMail?.Invoke(this, new NewMailEventArgs(account, messages));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
