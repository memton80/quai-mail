using MimeKit.Cryptography;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Drafts;
using QuaiMail.Core.Pgp;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Tests.Support;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.Core.Tests.ViewModels;

/// <summary>Bouton « Signer » de la rédaction : phrase de passe demandée à l'envoi, jamais rien envoyé sans signature voulue.</summary>
public sealed class ComposerSigningTests : IDisposable
{
    private const string Passphrase = "phrase de passe";
    private static readonly Org.BouncyCastle.Bcpg.OpenPgp.PgpSecretKeyRing Key = TestPgpKeys.Create("Alice <alice@example.org>", Passphrase);

    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly AccountStore _accounts;
    private readonly DraftStore _drafts;
    private readonly FakeMailService _mail = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeFileDialogs _files = new();
    private readonly ShellViewModel _shell;
    private readonly PgpKeyStore _pgp;

    public ComposerSigningTests()
    {
        _paths = new AppPaths(_temp.Path);
        _accounts = new AccountStore(_paths);
        _drafts = new DraftStore(_paths);
        _shell = new ShellViewModel(_dialogs, new KeyStrings(), _drafts, new WindowStateStore(_paths));
        _pgp = new PgpKeyStore(_paths);
        _accounts.Save(Account("alice@example.org"));
        _accounts.Save(Account("contact@example.org"));
    }

    public void Dispose()
    {
        _pgp.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task With_a_key_for_the_sender_mails_are_signed_and_the_passphrase_asked_once()
    {
        _pgp.Import(TestPgpKeys.Armored(Key));
        var composer = await NewMessageAsync();

        Assert.True(composer is { CanSign: true, Sign: true });
        var tooltip = composer.SignTooltip;
        Assert.StartsWith("Composer_SignWith(Alice <alice@example.org>,", tooltip, StringComparison.Ordinal);
        _dialogs.Passphrases.Enqueue(Passphrase);
        await composer.SendAsync();

        var sent = Assert.Single(_mail.Sent);
        Assert.IsType<MultipartSigned>(sent.Body);
        Assert.Equal([(tooltip, false)], _dialogs.AskedPassphrases);

        // Pendant la session, la phrase de passe n'est plus demandée.
        composer = await NewMessageAsync();
        await composer.SendAsync();
        Assert.Equal(2, _mail.Sent.Count);
        Assert.IsType<MultipartSigned>(_mail.Sent[1].Body);
        Assert.Single(_dialogs.AskedPassphrases);
    }

    [Fact]
    public async Task A_wrong_passphrase_is_asked_again_and_cancelling_sends_nothing()
    {
        _pgp.Import(TestPgpKeys.Armored(Key));
        var composer = await NewMessageAsync();

        _dialogs.Passphrases.Enqueue("pas la bonne");
        _dialogs.Passphrases.Enqueue(null);
        await composer.SendAsync();

        Assert.Empty(_mail.Sent);
        Assert.Equal([false, true], _dialogs.AskedPassphrases.Select(a => a.Retry));
        Assert.True(composer.IsOpen);
        Assert.Null(composer.ErrorMessage);

        // Trois phrases refusées : erreur claire, toujours rien d'envoyé.
        _dialogs.Passphrases.Enqueue("non");
        _dialogs.Passphrases.Enqueue("toujours pas");
        _dialogs.Passphrases.Enqueue("raté");
        await composer.SendAsync();
        Assert.Empty(_mail.Sent);
        Assert.Equal("PgpError_WrongPassphrase", composer.ErrorMessage);

        _dialogs.Passphrases.Enqueue(Passphrase);
        await composer.SendAsync();
        Assert.IsType<MultipartSigned>(Assert.Single(_mail.Sent).Body);
        Assert.False(composer.IsOpen);
    }

    [Fact]
    public async Task Unchecked_or_without_a_key_the_mail_leaves_unsigned()
    {
        _pgp.Import(TestPgpKeys.Armored(Key));
        var composer = await NewMessageAsync();
        composer.Sign = false;
        Assert.False(composer.Draft!.Sign);

        await composer.SendAsync();

        Assert.IsNotType<MultipartSigned>(Assert.Single(_mail.Sent).Body);
        Assert.Empty(_dialogs.AskedPassphrases);

        // Autre adresse d'envoi, sans clé : rien à signer.
        composer = await NewMessageAsync();
        composer.AccountIndex = 1;
        Assert.False(composer.CanSign || composer.Sign);
        Assert.Equal("Composer_SignNoKey", composer.SignTooltip);
    }

    [Fact]
    public async Task The_choice_is_kept_with_the_draft()
    {
        _pgp.Import(TestPgpKeys.Armored(Key));
        var composer = await NewMessageAsync();
        composer.Sign = false;
        var draft = composer.Draft!;

        _shell.CurrentDraft = null;
        _shell.CurrentDraft = draft;

        Assert.True(composer.CanSign);
        Assert.False(composer.Sign);
    }

    [Fact]
    public async Task Keys_are_imported_listed_and_removed_from_the_settings()
    {
        var keys = new PgpKeysViewModel(_pgp, _files, new KeyStrings());
        var file = Path.Combine(_temp.Path, "alice-secret.asc");
        await File.WriteAllBytesAsync(file, TestPgpKeys.Armored(Key).ToArray(), TestContext.Current.CancellationToken);

        _files.KeyFile = file;
        await keys.ImportAsync();

        var item = Assert.Single(keys.Keys);
        Assert.True(keys.HasKeys);
        Assert.Equal("Pgp_Imported(Alice <alice@example.org>)", keys.Message);
        Assert.False(keys.IsError);
        Assert.Equal("RSA 2048 · Pgp_NoExpiry", item.Details);
        Assert.Equal($"Pgp_RemoveMessage(Alice <alice@example.org>,{item.Fingerprint})", item.RemoveMessage);

        // Une clé publique seule : refusée, avec l'explication.
        await File.WriteAllBytesAsync(file, TestPgpKeys.ArmoredPublic(Key).ToArray(), TestContext.Current.CancellationToken);
        await keys.ImportAsync();
        Assert.Equal("PgpError_NoSecretKey", keys.Message);
        Assert.True(keys.IsError);

        // Retrait (confirmé dans la vue) : la clé ne signe plus.
        Assert.True(keys.Remove(item.Key));
        Assert.Empty(keys.Keys);
        Assert.False(keys.HasKeys);
        Assert.Null(_pgp.FindForAddress("alice@example.org", DateTimeOffset.UtcNow));
    }

    private async Task<ComposerViewModel> NewMessageAsync()
    {
        var composer = new ComposerViewModel(_accounts, _drafts, _mail, _shell, _dialogs, _files, new KeyStrings(), null, null, _pgp);
        Assert.True(await composer.NewAsync(null));
        composer.To = "bob@example.org";
        composer.Subject = "Signé";
        composer.Body = "Texte signé";
        return composer;
    }

    private static Account Account(string address) => new()
    {
        Address = address,
        UserName = address,
        Imap = new MailServer { Host = "imap.example.org", Port = 993 },
        Smtp = new MailServer { Host = "smtp.example.org", Port = 465 },
    };
}
