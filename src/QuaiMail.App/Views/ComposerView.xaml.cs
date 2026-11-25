using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuaiMail.Core.Drafts;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.App.Views;

/// <summary>
/// Rédaction d'un message (dans le volet de lecture). Le brouillon est enregistré en local toutes les quelques
/// secondes par la fenêtre ; il n'est envoyé que par le bouton Envoyer (volontairement sans raccourci clavier : une
/// touche tapée par erreur dans le texte ne doit jamais envoyer un mail).
/// </summary>
public sealed partial class ComposerView : UserControl
{
    private readonly ILogger _logger;

    public ComposerView()
    {
        var services = App.Current.Services;
        Composer = services.GetRequiredService<ComposerViewModel>();
        _logger = services.GetRequiredService<ILogger<ComposerView>>();
        InitializeComponent();
        Composer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ComposerViewModel.IsOpen) && Composer.IsOpen)
            {
                // Nouveau message : on commence par les destinataires ; réponse : par le texte.
                DispatcherQueue.TryEnqueue(() => (string.IsNullOrEmpty(Composer.To) ? ToBox : (Control)BodyBox).Focus(FocusState.Programmatic));
            }
        };
    }

    public ComposerViewModel Composer { get; }

    private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.DataContext is DraftAttachment attachment)
            {
                Composer.RemoveAttachment(attachment);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Pièce jointe non retirée ({Error})", ex.GetType().Name);
        }
    }
}
