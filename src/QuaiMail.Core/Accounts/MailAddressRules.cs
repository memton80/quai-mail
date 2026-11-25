using System.Globalization;

namespace QuaiMail.Core.Accounts;

/// <summary>Contrôles simples des adresses et noms de serveurs saisis (avant toute connexion).</summary>
public static class MailAddressRules
{
    private static readonly IdnMapping Idn = new();

    /// <summary>« local@domaine » avec un domaine plausible (au moins un point) ; ni espace ni chevron.</summary>
    public static bool TryGetDomain(string? address, out string domain)
    {
        domain = string.Empty;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var trimmed = address.Trim();
        var at = trimmed.LastIndexOf('@');
        if (at <= 0 || at == trimmed.Length - 1 || trimmed.AsSpan().IndexOfAny(" \t<>\"(),;:") >= 0)
        {
            return false;
        }

        if (!TryNormalizeHost(trimmed[(at + 1)..], out var host) || !host.Contains('.'))
        {
            return false;
        }

        domain = host;
        return true;
    }

    public static bool IsValidAddress(string? address) => TryGetDomain(address, out _);

    /// <summary>Nom d'hôte DNS (lettres, chiffres, tirets, points ; noms internationaux convertis en ASCII).</summary>
    public static bool IsValidHost(string? host) => TryNormalizeHost(host, out _);

    public static bool TryNormalizeHost(string? host, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string ascii;
        try
        {
            ascii = Idn.GetAscii(host.Trim().TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (ascii.Length is 0 or > 253)
        {
            return false;
        }

        foreach (var label in ascii.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var c in label)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                {
                    return false;
                }
            }
        }

        normalized = ascii;
        return true;
    }
}
