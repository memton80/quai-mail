using QuaiMail.Core.Accounts;
using QuaiMail.Core.Reading;

namespace QuaiMail.Core.Mail;

/// <summary>
/// Accès aux serveurs d'un compte : lecture, et seulement les drapeaux lu / non lu et étoile côté serveur ; aucun envoi.
/// Les erreurs de connexion arrivent en <see cref="MailConnectionException"/>.
/// </summary>
public interface IMailService
{
    /// <summary>
    /// Vérifie les réglages d'un compte avant de l'enregistrer : connexion et identification IMAP, puis SMTP
    /// (identification seulement, aucun mail envoyé).
    /// </summary>
    Task VerifyAsync(Account account, string password, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(Account account, CancellationToken cancellationToken = default);

    /// <summary>En-têtes d'un dossier, du plus récent au plus ancien : <paramref name="skip"/> déjà affichés, puis <paramref name="take"/>.</summary>
    Task<MessagePage> GetMessagesAsync(Account account, string folder, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Un mail complet (corps, pièces jointes), sans le marquer lu : c'est l'application qui le décide.</summary>
    Task<MessageContent> GetMessageAsync(Account account, string folder, uint uid, CancellationToken cancellationToken = default);

    /// <summary>Marque un mail lu ou non lu, avec ou sans étoile (<c>null</c> : inchangé).</summary>
    Task SetFlagsAsync(Account account, string folder, uint uid, bool? seen, bool? flagged, CancellationToken cancellationToken = default);

    /// <summary>Ferme la connexion gardée ouverte pour ce compte (compte supprimé, fermeture de l'application).</summary>
    Task DisconnectAsync(string accountId);
}
