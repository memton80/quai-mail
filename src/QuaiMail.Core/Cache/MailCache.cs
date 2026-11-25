using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Mail;

namespace QuaiMail.Core.Cache;

/// <summary>
/// Cache local des dossiers et des en-têtes (SQLite, <c>%LOCALAPPDATA%\QuaiMail\cache\mail.db</c>) : il permet
/// d'afficher tout de suite ce qui était là la dernière fois. Le serveur reste la référence : le fichier peut être
/// effacé à tout moment (<see cref="Clear"/>), il se reconstruit. Une base d'un autre format, ou illisible, est
/// simplement recréée.
/// </summary>
public sealed class MailCache
{
    /// <summary>Version du schéma (<c>PRAGMA user_version</c>) : une autre valeur fait recréer la base.</summary>
    internal const int SchemaVersion = 1;

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private bool _ready;

    public MailCache(string path, ILogger<MailCache>? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<MailCache>.Instance;
    }

    public string FilePath => _path;

    public void SaveFolders(string accountId, IReadOnlyList<MailFolderInfo> folders)
    {
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "DELETE FROM folders WHERE account = $account", ("$account", accountId));
            var order = 0;
            foreach (var folder in folders)
            {
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO folders (account, full_name, name, depth, role, can_select, unread, total, position)
                    VALUES ($account, $fullName, $name, $depth, $role, $canSelect, $unread, $total, $position)
                    """,
                    ("$account", accountId),
                    ("$fullName", folder.FullName),
                    ("$name", folder.Name),
                    ("$depth", folder.Depth),
                    ("$role", (int)folder.Role),
                    ("$canSelect", folder.CanSelect ? 1 : 0),
                    ("$unread", folder.Unread),
                    ("$total", folder.Total),
                    ("$position", order++));
            }

            // Messages des dossiers disparus du serveur.
            Execute(
                connection,
                transaction,
                "DELETE FROM messages WHERE account = $account AND folder NOT IN (SELECT full_name FROM folders WHERE account = $account)",
                ("$account", accountId));
            transaction.Commit();
        });
    }

    public IReadOnlyList<MailFolderInfo> GetFolders(string accountId) =>
        Query(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT full_name, name, depth, role, can_select, unread, total FROM folders WHERE account = $account ORDER BY position";
            command.Parameters.AddWithValue("$account", accountId);
            using var reader = command.ExecuteReader();
            var folders = new List<MailFolderInfo>();
            while (reader.Read())
            {
                folders.Add(new MailFolderInfo(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    Enum.IsDefined((FolderRole)reader.GetInt32(3)) ? (FolderRole)reader.GetInt32(3) : FolderRole.None,
                    reader.GetInt32(4) != 0,
                    reader.GetInt32(5),
                    reader.GetInt32(6)));
            }

            return (IReadOnlyList<MailFolderInfo>)folders;
        }) ?? [];

    /// <summary>
    /// Ajoute ou met à jour des en-têtes. Si l'UIDVALIDITY du dossier a changé, les en-têtes déjà en cache ne
    /// correspondent plus à rien : ils sont effacés d'abord.
    /// </summary>
    public void SaveMessages(string accountId, string folder, uint uidValidity, IReadOnlyList<MessageHeader> messages)
    {
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction();
            Execute(
                connection,
                transaction,
                "DELETE FROM messages WHERE account = $account AND folder = $folder AND uid_validity <> $validity",
                ("$account", accountId),
                ("$folder", folder),
                ("$validity", (long)uidValidity));
            foreach (var message in messages)
            {
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO messages (account, folder, uid, uid_validity, subject, from_name, from_address, date, is_read, is_flagged, size)
                    VALUES ($account, $folder, $uid, $validity, $subject, $fromName, $fromAddress, $date, $isRead, $isFlagged, $size)
                    ON CONFLICT (account, folder, uid) DO UPDATE SET
                        uid_validity = excluded.uid_validity, subject = excluded.subject, from_name = excluded.from_name,
                        from_address = excluded.from_address, date = excluded.date, is_read = excluded.is_read,
                        is_flagged = excluded.is_flagged, size = excluded.size
                    """,
                    ("$account", accountId),
                    ("$folder", folder),
                    ("$uid", (long)message.Uid),
                    ("$validity", (long)uidValidity),
                    ("$subject", message.Subject),
                    ("$fromName", message.FromName),
                    ("$fromAddress", message.FromAddress),
                    ("$date", message.Date.ToUnixTimeMilliseconds()),
                    ("$isRead", message.IsRead ? 1 : 0),
                    ("$isFlagged", message.IsFlagged ? 1 : 0),
                    ("$size", message.Size));
            }

            transaction.Commit();
        });
    }

    /// <summary>En-têtes en cache, du plus récent (plus grand UID) au plus ancien.</summary>
    public IReadOnlyList<MessageHeader> GetMessages(string accountId, string folder, int skip, int take) =>
        Query(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT uid, subject, from_name, from_address, date, is_read, is_flagged, size FROM messages
                WHERE account = $account AND folder = $folder ORDER BY uid DESC LIMIT $take OFFSET $skip
                """;
            command.Parameters.AddWithValue("$account", accountId);
            command.Parameters.AddWithValue("$folder", folder);
            command.Parameters.AddWithValue("$take", take);
            command.Parameters.AddWithValue("$skip", skip);
            using var reader = command.ExecuteReader();
            var messages = new List<MessageHeader>();
            while (reader.Read())
            {
                messages.Add(new MessageHeader(
                    (uint)reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    reader.GetInt32(5) != 0,
                    reader.GetInt32(6) != 0,
                    reader.GetInt64(7)));
            }

            return (IReadOnlyList<MessageHeader>)messages;
        }) ?? [];

    /// <summary>Tout ce qui concerne un compte (compte supprimé).</summary>
    public void RemoveAccount(string accountId) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "DELETE FROM messages WHERE account = $account", ("$account", accountId));
            Execute(connection, transaction, "DELETE FROM folders WHERE account = $account", ("$account", accountId));
            transaction.Commit();
        });

    /// <summary>Efface le fichier du cache : tout sera relu depuis les serveurs.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { _path, _path + "-wal", _path + "-shm", _path + "-journal" })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }

            _ready = false;
        }

        _logger.LogInformation("Cache local effacé");
    }

    private SqliteConnection Open()
    {
        lock (_gate)
        {
            if (!_ready)
            {
                Prepare();
                _ready = true;
            }
        }

        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();

    /// <summary>Crée la base, ou la recrée si elle est d'une autre version ou illisible.</summary>
    private void Prepare()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        try
        {
            if (TryPrepare())
            {
                return;
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning("Cache local illisible ({Code}), recréé", ex.SqliteErrorCode);
        }

        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm", _path + "-journal" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        if (!TryPrepare())
        {
            throw new InvalidOperationException("Cache local impossible à créer.");
        }
    }

    private bool TryPrepare()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
            var version = QueryVersion(connection);
            if (version == SchemaVersion)
            {
                return true;
            }

            if (version != 0)
            {
                _logger.LogInformation("Cache local d'une autre version ({Version}), recréé", version);
                return false;
            }
        }

        using var create = connection.CreateCommand();
        create.CommandText = $"""
            CREATE TABLE folders (
                account TEXT NOT NULL, full_name TEXT NOT NULL, name TEXT NOT NULL, depth INTEGER NOT NULL,
                role INTEGER NOT NULL, can_select INTEGER NOT NULL, unread INTEGER NOT NULL, total INTEGER NOT NULL,
                position INTEGER NOT NULL, PRIMARY KEY (account, full_name));
            CREATE TABLE messages (
                account TEXT NOT NULL, folder TEXT NOT NULL, uid INTEGER NOT NULL, uid_validity INTEGER NOT NULL,
                subject TEXT NOT NULL, from_name TEXT NOT NULL, from_address TEXT NOT NULL, date INTEGER NOT NULL,
                is_read INTEGER NOT NULL, is_flagged INTEGER NOT NULL, size INTEGER NOT NULL,
                PRIMARY KEY (account, folder, uid));
            PRAGMA user_version = {SchemaVersion};
            """;
        create.ExecuteNonQuery();
        return true;
    }

    private static long QueryVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private void Run(Action<SqliteConnection> action) => Query(connection =>
    {
        action(connection);
        return string.Empty;
    });

    /// <summary>Une erreur de cache n'empêche jamais de lire ses mails : elle est journalisée, et l'appelant passe au serveur.</summary>
    private T? Query<T>(Func<SqliteConnection, T> query)
        where T : class
    {
        try
        {
            using var connection = Open();
            return query(connection);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning("Cache local indisponible ({Error})", ex.GetType().Name);
            return null;
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
