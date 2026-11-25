namespace QuaiMail.Core.Services;

/// <summary>Ouverture d'une adresse hors de Quai Mail (navigateur, messagerie par défaut), implémentée par l'application.</summary>
public interface ILauncher
{
    /// <summary>Ouvre une adresse http, https ou mailto ; toute autre adresse est refusée.</summary>
    bool Open(Uri uri);
}
