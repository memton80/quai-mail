using QuaiMail.Core.Localization;
using QuaiMail.Core.Mail;

namespace QuaiMail.Core.ViewModels;

/// <summary>Messages d'erreur de connexion compréhensibles (ressources « MailError_… »).</summary>
public static class MailErrors
{
    public static string Describe(IStrings strings, MailConnectionException error) =>
        strings.Format("MailError_Format", error.Protocol, error.Server, strings.Get("MailError_" + error.Error));
}
