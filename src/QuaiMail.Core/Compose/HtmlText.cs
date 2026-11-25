using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace QuaiMail.Core.Compose;

/// <summary>
/// Texte d'un mail HTML (citation dans une réponse, transfert) : AngleSharp (déjà là pour le nettoyage) lit le HTML,
/// les blocs deviennent des lignes ; scripts et styles sont ignorés. Rien n'est exécuté ni chargé.
/// </summary>
public static class HtmlText
{
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "table", "ul", "ol", "section", "article", "header", "footer",
    };

    public static string ToPlainText(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        var text = new StringBuilder();
        Walk(document.Body ?? (INode)document, text);
        var lines = text.ToString().Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd());
        var collapsed = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0 && collapsed.Count > 0 && collapsed[^1].Length == 0)
            {
                continue;
            }

            collapsed.Add(line);
        }

        return string.Join('\n', collapsed).Trim('\n');
    }

    private static void Walk(INode node, StringBuilder text)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText leaf:
                    text.Append(CollapseSpaces(leaf.Data));
                    break;
                case IElement element when element.LocalName is "script" or "style" or "head" or "title":
                    break;
                case IElement { LocalName: "br" }:
                    text.Append('\n');
                    break;
                case IElement element:
                    var block = Blocks.Contains(element.LocalName);
                    if (block && text.Length > 0 && text[^1] != '\n')
                    {
                        text.Append('\n');
                    }

                    Walk(element, text);
                    if (element.LocalName == "a" && element.GetAttribute("href") is { } href
                        && href.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !element.TextContent.Contains(href, StringComparison.Ordinal))
                    {
                        text.Append(" <").Append(href).Append('>');
                    }

                    if (block)
                    {
                        text.Append('\n');
                    }

                    break;
            }
        }
    }

    private static string CollapseSpaces(string data)
    {
        var builder = new StringBuilder(data.Length);
        var space = false;
        foreach (var c in data)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!space)
                {
                    builder.Append(' ');
                }

                space = true;
            }
            else
            {
                builder.Append(c);
                space = false;
            }
        }

        return builder.ToString();
    }
}
