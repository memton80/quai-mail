using QuaiMail.Core.Accounts;
using QuaiMail.Core.Reading;

namespace QuaiMail.Core.Mail;

/// <summary>
/// Accès aux serveurs d'un compte : lecture, drapeaux lu / non lu et étoile, copies dans Brouillons et Envoyés, et
/// l'envoi, uniquement par <see cref="SendAsync"/> (appelé seulement par le bouton Envoyer). Les erreurs de connexion
/// arrivent en <see cref="MailConnectionException"/>.
/// </summary>
public interface IMailService
{
    /// <summary>
    /// Vérifie les réglages d'un compte avant de l'enregistrer : connexion et identification IMAP, puis SMTP
    /// (identification seulement, aucun mail envoyé).
    /// </summary>
    Task VerifyAsync(Account account, string password, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(Account account, CancellationToken cancellationToken = default);

    /// <summary>
    /// En-têtes d'un dossier, du plus récent au plus ancien : <paramref name="skip"/> déjà affichés, puis
    /// <paramref name="take"/>. Avec un filtre, seuls les mails lus (ou non lus) comptent, recherchés sur le serveur.
    /// </summary>
    Task<MessagePage> GetMessagesAsync(Account account, string folder, int skip, int take, ReadFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Un mail complet (corps, pièces jointes), sans le marquer lu : c'est l'application qui le décide.</summary>
    Task<MessageContent> GetMessageAsync(Account account, string folder, uint uid, CancellationToken cancellationToken = default);

    /// <summary>Marque un mail lu ou non lu, avec ou sans étoile (<c>null</c> : inchangé).</summary>
    Task SetFlagsAsync(Account account, string folder, uint uid, bool? seen, bool? flagged, CancellationToken cancellationToken = default);

    /// <summary>Même chose pour plusieurs mails d'un même dossier (sélection) : une seule commande au serveur.</summary>
    Task SetFlagsAsync(Account account, string folder, IReadOnlyList<uint> uids, bool? seen, bool? flagged, CancellationToken cancellationToken = default);

    /// <summary>
    /// Envoie un mail par le serveur SMTP du compte, puis en garde une copie dans le dossier Envoyés (sauf chez les
    /// fournisseurs qui le font eux-mêmes). Seul le bouton Envoyer y mène.
    /// </summary>
    Task<SendOutcome> SendAsync(Account account, MimeKit.MimeMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copie un brouillon dans le dossier Brouillons du serveur et retire sa copie précédente (par son UID seulement).
    /// </summary>
    /// <returns>La copie, ou <c>null</c> si le serveur n'a pas de dossier Brouillons.</returns>
    Task<ServerCopy?> SaveDraftAsync(Account account, MimeKit.MimeMessage message, ServerCopy? previous, CancellationToken cancellationToken = default);

    /// <summary>Retire un brouillon du serveur (envoyé ou abandonné), par son UID seulement.</summary>
    Task DeleteDraftAsync(Account account, ServerCopy copy, CancellationToken cancellationToken = default);

    /// <summary>Déplace un mail vers un autre dossier du même compte.</summary>
    Task MoveAsync(Account account, string folder, uint uid, string target, CancellationToken cancellationToken = default);

    /// <summary>Déplace plusieurs mails d'un même dossier (sélection) en une seule commande.</summary>
    Task MoveAsync(Account account, string folder, IReadOnlyList<uint> uids, string target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Supprimer, première étape : vers la corbeille du serveur.
    /// </summary>
    /// <returns><c>false</c> s'il n'y a pas de corbeille ou si le mail y est déjà : il faut alors une suppression définitive, après confirmation.</returns>
    Task<bool> MoveToTrashAsync(Account account, string folder, uint uid, CancellationToken cancellationToken = default);

    /// <summary>Plusieurs mails d'un même dossier (sélection) vers la corbeille, en une seule commande.</summary>
    /// <returns><c>false</c> (rien n'a bougé) s'il n'y a pas de corbeille ou si le dossier est la corbeille.</returns>
    Task<bool> MoveToTrashAsync(Account account, string folder, IReadOnlyList<uint> uids, CancellationToken cancellationToken = default);

    /// <summary>Suppression définitive (après confirmation) : ce seul mail, par son UID ; jamais d'effacement général.</summary>
    Task DeletePermanentlyAsync(Account account, string folder, uint uid, CancellationToken cancellationToken = default);

    /// <summary>Suppression définitive (après confirmation) de ces seuls mails, par leurs UID ; jamais d'effacement général.</summary>
    Task DeletePermanentlyAsync(Account account, string folder, IReadOnlyList<uint> uids, CancellationToken cancellationToken = default);

    /// <summary>Recherche sur le serveur dans un dossier (objet, expéditeur, texte) : les plus récents d'abord.</summary>
    Task<IReadOnlyList<MessageHeader>> SearchAsync(Account account, string folder, string text, int limit, CancellationToken cancellationToken = default);

    /// <summary>Ferme la connexion gardée ouverte pour ce compte (compte supprimé, fermeture de l'application).</summary>
    Task DisconnectAsync(string accountId);
}

/// <summary>Un mail copié dans un dossier du serveur.</summary>
/// <param name="Uid">UID donné par le serveur (extension UIDPLUS), sinon <c>null</c>.</param>
public sealed record ServerCopy(string Folder, uint? Uid);

/// <summary>Résultat d'un envoi : le mail est parti ; sa copie dans Envoyés a-t-elle été faite ?</summary>
/// <param name="SentCopy">Dossier où la copie a été rangée, <c>null</c> si elle n'a pas pu l'être.</param>
/// <param name="ServerKeepsCopy">Le fournisseur range lui-même les mails envoyés (Gmail, Outlook.com).</param>
public sealed record SendOutcome(string? SentCopy, bool ServerKeepsCopy = false);
