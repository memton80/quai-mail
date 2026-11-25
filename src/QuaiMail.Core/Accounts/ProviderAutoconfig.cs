using System.Net;
using System.Net.Http.Headers;
using System.Xml;
using System.Xml.Linq;

namespace QuaiMail.Core.Accounts;

/// <summary>
/// Autoconfiguration publiée par le fournisseur lui-même (format « config-v1.1.xml » de Thunderbird), lue en HTTPS
/// sur son propre domaine : <c>https://autoconfig.&lt;domaine&gt;/mail/config-v1.1.xml</c>, puis
/// <c>https://&lt;domaine&gt;/.well-known/autoconfig/mail/config-v1.1.xml</c>. Aucun service tiers n'est interrogé, et
/// l'adresse elle-même n'est pas envoyée (seul le domaine apparaît dans l'URL). Seuls les serveurs chiffrés (SSL /
/// STARTTLS) sont retenus.
/// </summary>
public sealed class ProviderAutoconfig : IDisposable
{
    /// <summary>Au-delà, la réponse n'est pas un fichier d'autoconfiguration : elle est ignorée.</summary>
    internal const int MaxResponseBytes = 64 * 1024;

    private readonly HttpClient _http;

    public ProviderAutoconfig(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.Version));
    }

    /// <summary>Adresses interrogées, dans l'ordre (construites ici à partir du seul domaine).</summary>
    public static IReadOnlyList<Uri> UrlsFor(string domain) =>
    [
        new($"https://autoconfig.{domain}/mail/config-v1.1.xml"),
        new($"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml"),
    ];

    /// <returns>Les réglages publiés, ou <c>null</c> (rien de publié, réseau indisponible, réponse inutilisable).</returns>
    public async Task<MailSettings?> TryGetAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!MailAddressRules.TryGetDomain(address, out var domain))
        {
            return null;
        }

        foreach (var url in UrlsFor(domain))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? xml;
            try
            {
                xml = await DownloadAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            if (xml is not null && Parse(xml, address.Trim()) is { } settings)
            {
                return settings;
            }
        }

        return null;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Lit un fichier « config-v1.1.xml » ; <c>null</c> s'il ne décrit pas un IMAP et un SMTP chiffrés.</summary>
    public static MailSettings? Parse(string xml, string address)
    {
        if (!MailAddressRules.TryGetDomain(address, out var domain))
        {
            return null;
        }

        XDocument document;
        try
        {
            // Pas de DTD ni d'entités externes.
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var provider = document.Root?.Element("emailProvider");
        if (provider is null)
        {
            return null;
        }

        var local = address[..address.LastIndexOf('@')];
        string Expand(string? value) => (value ?? string.Empty).Trim()
            .Replace("%EMAILADDRESS%", address, StringComparison.Ordinal)
            .Replace("%EMAILLOCALPART%", local, StringComparison.Ordinal)
            .Replace("%EMAILDOMAIN%", domain, StringComparison.Ordinal);

        (MailServer Server, string UserName, bool OAuthOnly)? Pick(string element, string type)
        {
            var candidates = provider.Elements(element)
                .Where(e => string.Equals((string?)e.Attribute("type"), type, StringComparison.OrdinalIgnoreCase))
                .Select(e =>
                {
                    var security = ((string?)e.Element("socketType"))?.Trim().ToUpperInvariant() switch
                    {
                        "SSL" or "TLS" => MailSecurity.SslOnConnect,
                        "STARTTLS" => MailSecurity.StartTls,
                        _ => (MailSecurity?)null,
                    };
                    var authentication = e.Elements("authentication").Select(a => a.Value.Trim().ToLowerInvariant()).ToList();
                    var oauthOnly = authentication.Count > 0 && authentication.All(a => a == "oauth2");
                    var host = Expand((string?)e.Element("hostname"));
                    return int.TryParse((string?)e.Element("port"), out var port) && security is { } s && MailAddressRules.TryNormalizeHost(host, out var h)
                        ? (Server: new MailServer { Host = h, Port = port, Security = s }, UserName: Expand((string?)e.Element("username")), OAuthOnly: oauthOnly)
                        : ((MailServer Server, string UserName, bool OAuthOnly)?)null;
                })
                .OfType<(MailServer Server, string UserName, bool OAuthOnly)>()
                .Where(c => c.Server.IsValid)
                .ToList();

            // TLS dès la connexion d'abord, puis STARTTLS.
            return candidates.Count == 0 ? null : candidates.OrderBy(c => c.Server.Security == MailSecurity.SslOnConnect ? 0 : 1).First();
        }

        if (Pick("incomingServer", "imap") is not { } imap || Pick("outgoingServer", "smtp") is not { } smtp)
        {
            return null;
        }

        return new MailSettings
        {
            Imap = imap.Server,
            Smtp = smtp.Server,
            UserName = string.IsNullOrWhiteSpace(imap.UserName) ? address : imap.UserName,
            Source = MailSettingsSource.ProviderAutoconfig,
            ProviderName = ((string?)provider.Element("displayName"))?.Trim() is { Length: > 0 and <= 80 } name ? name : null,
            RequiresOAuth = imap.OAuthOnly,
        };
    }

    private async Task<string?> DownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK
            || response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps
            || response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxResponseBytes + 1];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
            {
                return null;
            }
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }
}
