using MimeKit;
using QuaiMail.Core.Accounts;

namespace QuaiMail.Core.Compose;

/// <summary>Destinataires saisis (« Léa &lt;lea@example.org&gt;, bob@example.org ») : lus et vérifiés.</summary>
public static class Recipients
{
    /// <summary>Lit une liste d'adresses séparées par des virgules ou des points-virgules.</summary>
    /// <param name="invalid">Morceaux qui ne sont pas des adresses valides (affichés tels quels à l'utilisateur).</param>
    public static IReadOnlyList<MailboxAddress> Parse(string? text, out IReadOnlyList<string> invalid)
    {
        var valid = new List<MailboxAddress>();
        var wrong = new List<string>();
        foreach (var part in Split(text ?? string.Empty))
        {
            if (InternetAddressList.TryParse(part, out var parsed)
                && parsed.Mailboxes.ToList() is { Count: 1 } mailboxes
                && MailAddressRules.IsValidAddress(mailboxes[0].Address))
            {
                valid.Add(mailboxes[0]);
            }
            else
            {
                wrong.Add(part);
            }
        }

        invalid = wrong;
        return valid;
    }

    /// <summary>Écrit une liste d'adresses lisible et relisible (« Nom &lt;adresse&gt;, … »).</summary>
    public static string Format(IEnumerable<MailboxAddress> addresses) =>
        string.Join(", ", addresses.Select(a => string.IsNullOrWhiteSpace(a.Name) ? a.Address : $"{Name(a.Name.Trim())} <{a.Address}>"));

    /// <summary>Nom tel quel, entre guillemets seulement s'il contient un caractère qui couperait l'adresse.</summary>
    private static string Name(string name) =>
        name.AsSpan().IndexOfAny(",;<>\"@:()[]\\") >= 0
            ? "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : name;

    /// <summary>Découpe aux virgules et points-virgules, sauf entre guillemets ou chevrons.</summary>
    private static IEnumerable<string> Split(string text)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var angle = false;
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    quoted = !quoted;
                    break;
                case '<' when !quoted:
                    angle = true;
                    break;
                case '>' when !quoted:
                    angle = false;
                    break;
                case ',' or ';' when !quoted && !angle:
                    if (current.ToString().Trim() is { Length: > 0 } part)
                    {
                        yield return part;
                    }

                    current.Clear();
                    continue;
            }

            current.Append(c);
        }

        if (current.ToString().Trim() is { Length: > 0 } last)
        {
            yield return last;
        }
    }
}
