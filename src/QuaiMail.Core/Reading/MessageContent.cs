using MimeKit;
using MimeKit.Text;

namespace QuaiMail.Core.Reading;

/// <summary>Une pièce jointe du mail ouvert.</summary>
/// <param name="IsExecutable">Type qui exécute du code à l'ouverture : avertissement avant de l'enregistrer.</param>
public sealed record AttachmentInfo(int Index, string FileName, string ContentType, long Size, bool IsExecutable);

/// <summary>
/// Un mail ouvert : en-têtes à afficher, corps (HTML et / ou texte), pièces jointes et images jointes. Lu par MimeKit ;
/// le HTML n'est jamais affiché tel quel (voir <see cref="BodyHtml"/>).
/// </summary>
public sealed class MessageContent
{
    private readonly IReadOnlyList<MimeEntity> _attachments;
    private readonly Dictionary<string, MimePart> _images;

    private MessageContent(uint uid, MimeMessage message)
    {
        Uid = uid;
        Subject = message.Subject ?? string.Empty;
        var from = message.From.Mailboxes.FirstOrDefault();
        FromName = from?.Name ?? string.Empty;
        FromAddress = from?.Address ?? string.Empty;
        To = Describe(message.To);
        Cc = Describe(message.Cc);
        Date = message.Date;
        Html = message.HtmlBody;
        Text = message.TextBody;

        _images = message.BodyParts
            .OfType<MimePart>()
            .Where(p => !string.IsNullOrEmpty(p.ContentId) && p.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.ContentId!.Trim('<', '>', ' '), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        _attachments = [.. message.Attachments];
        Attachments = [.. _attachments.Select((entity, index) =>
        {
            var name = AttachmentRules.SafeFileName(entity switch
            {
                MimePart part => part.FileName,
                MessagePart => (entity.ContentType.Name ?? "message") + ".eml",
                _ => entity.ContentType.Name,
            });
            return new AttachmentInfo(index, name, entity.ContentType.MimeType, MeasureAsync(entity).GetAwaiter().GetResult(), AttachmentRules.IsExecutable(name));
        })];
    }

    public uint Uid { get; }

    public string Subject { get; }

    public string FromName { get; }

    public string FromAddress { get; }

    /// <summary>Expéditeur affiché : « Nom &lt;adresse&gt; », ou l'adresse seule.</summary>
    public string From => string.IsNullOrWhiteSpace(FromName) ? FromAddress : $"{FromName} <{FromAddress}>";

    public string To { get; }

    public string Cc { get; }

    public DateTimeOffset Date { get; }

    /// <summary>Corps HTML tel que reçu (jamais affiché sans nettoyage).</summary>
    public string? Html { get; }

    public string? Text { get; }

    public IReadOnlyList<AttachmentInfo> Attachments { get; }

    /// <summary>Identifiants des images jointes (référencées en <c>cid:</c> dans le HTML).</summary>
    public IReadOnlyCollection<string> ContentIds => _images.Keys;

    public static MessageContent FromMime(uint uid, MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new MessageContent(uid, message);
    }

    /// <summary>
    /// Corps à afficher, nettoyé : le HTML s'il y en a un, sinon le texte (liens rendus cliquables), sinon rien.
    /// </summary>
    public CleanedHtml BodyHtml(bool allowRemoteImages)
    {
        if (!string.IsNullOrWhiteSpace(Html))
        {
            return HtmlCleaner.Clean(Html, allowRemoteImages, ContentIds);
        }

        if (string.IsNullOrEmpty(Text))
        {
            return new CleanedHtml(string.Empty, 0);
        }

        var converter = new TextToHtml { OutputHtmlFragment = true };
        var html = $"<div class=\"mail-plain\">{converter.Convert(Text)}</div>";
        return HtmlCleaner.Clean(html, allowRemoteImages, ContentIds);
    }

    /// <summary>Image jointe (<c>cid:</c>) décodée, pour l'hôte local de la page.</summary>
    public async Task<(string ContentType, byte[] Data)?> GetImageAsync(string contentId, CancellationToken cancellationToken = default)
    {
        if (!_images.TryGetValue(contentId, out var part) || part.Content is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await part.Content.DecodeToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return (part.ContentType.MimeType, buffer.ToArray());
    }

    /// <summary>Écrit le contenu décodé d'une pièce jointe (enregistrement : voir <c>AtomicFile</c>).</summary>
    public async Task WriteAttachmentAsync(int index, Stream destination, CancellationToken cancellationToken = default)
    {
        switch (_attachments[index])
        {
            case MimePart { Content: { } content }:
                await content.DecodeToAsync(destination, cancellationToken).ConfigureAwait(false);
                break;
            case MessagePart { Message: { } attached }:
                await attached.WriteToAsync(destination, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("Pièce jointe vide.");
        }
    }

    private static string Describe(InternetAddressList list) =>
        string.Join(", ", list.Mailboxes.Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Address : $"{m.Name} <{m.Address}>"));

    private static async Task<long> MeasureAsync(MimeEntity entity)
    {
        await using var counter = new CountingStream();
        switch (entity)
        {
            case MimePart { Content: { } content }:
                await content.DecodeToAsync(counter).ConfigureAwait(false);
                break;
            case MessagePart { Message: { } attached }:
                await attached.WriteToAsync(counter).ConfigureAwait(false);
                break;
        }

        return counter.Length;
    }

    /// <summary>Compte les octets écrits, sans les garder.</summary>
    private sealed class CountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => _length += count;

        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _length += buffer.Length;
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _length += count;
            return Task.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
