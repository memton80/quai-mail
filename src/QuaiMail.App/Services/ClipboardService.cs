using QuaiMail.Core.Services;
using Windows.ApplicationModel.DataTransfer;

namespace QuaiMail.App.Services;

/// <summary>Presse-papiers de Windows : le texte copié reste disponible après la fermeture de Quai Mail.</summary>
internal sealed class ClipboardService : IClipboard
{
    public void SetText(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
