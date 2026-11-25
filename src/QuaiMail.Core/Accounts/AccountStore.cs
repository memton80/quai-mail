using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Storage;

namespace QuaiMail.Core.Accounts;

/// <summary>Contenu de <c>accounts.json</c>.</summary>
public sealed class AccountList
{
    public List<Account> Accounts { get; set; } = [];
}

/// <summary>Comptes configurés (<c>%LOCALAPPDATA%\QuaiMail\accounts.json</c>, sans aucun mot de passe).</summary>
public sealed class AccountStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private List<Account>? _accounts;

    public AccountStore(AppPaths paths, ILogger<AccountStore>? logger = null)
        : this(paths.AccountsFile, logger)
    {
    }

    public AccountStore(string path, ILogger<AccountStore>? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<AccountStore>.Instance;
    }

    /// <summary>Déclenché après chaque ajout, modification ou suppression enregistrée.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<Account> All
    {
        get
        {
            lock (_gate)
            {
                return [.. Load()];
            }
        }
    }

    public Account? Find(string? id)
    {
        if (id is null)
        {
            return null;
        }

        lock (_gate)
        {
            return Load().FirstOrDefault(a => a.Id == id);
        }
    }

    /// <summary>Ajoute ou remplace (même identifiant) un compte, puis enregistre le fichier.</summary>
    public void Save(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_gate)
        {
            var accounts = Load();
            var index = accounts.FindIndex(a => a.Id == account.Id);
            var updated = new List<Account>(accounts);
            if (index >= 0)
            {
                updated[index] = account;
            }
            else
            {
                updated.Add(account);
            }

            Write(updated);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <returns><c>true</c> si le compte existait.</returns>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var accounts = Load();
            var updated = accounts.Where(a => a.Id != id).ToList();
            if (updated.Count == accounts.Count)
            {
                return false;
            }

            Write(updated);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Write(List<Account> accounts)
    {
        // Écriture d'abord : en cas d'échec, la liste en mémoire reste celle du disque.
        JsonFile.Write(_path, new AccountList { Accounts = accounts }, QuaiMailJsonContext.Default.AccountList);
        _accounts = accounts;
    }

    private List<Account> Load()
    {
        if (_accounts is not null)
        {
            return _accounts;
        }

        try
        {
            _accounts = JsonFile.Read(_path, QuaiMailJsonContext.Default.AccountList)?.Accounts ?? [];
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Liste des comptes illisible ({Error}), mise de côté", ex.GetType().Name);
            try
            {
                JsonFile.Quarantine(_path);
            }
            catch (Exception quarantine) when (quarantine is IOException or UnauthorizedAccessException)
            {
                // Le fichier reste en place ; il sera remplacé au prochain enregistrement.
            }

            _accounts = [];
        }

        // Un compte mal formé (fichier modifié à la main) est ignoré plutôt que de tout bloquer.
        _accounts = [.. _accounts.Where(a => a is { Imap: not null, Smtp: not null } && !string.IsNullOrEmpty(a.Id))];
        return _accounts;
    }
}
