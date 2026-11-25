using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using Microsoft.Extensions.Logging;
using QuaiMail.Core.Services;

namespace QuaiMail.App.Services;

/// <summary>
/// Marque du web d'une pièce jointe enregistrée, posée par Windows lui-même : le gestionnaire de pièces jointes
/// (<c>IAttachmentExecute::Save</c>, celui d'Outlook et des navigateurs) applique la stratégie de sécurité, lance
/// l'antivirus sur le fichier et écrit la zone Internet. À l'ouverture, SmartScreen (programmes) et le mode protégé
/// d'Office s'appliquent. Si le gestionnaire ne répond pas, le flux <c>Zone.Identifier</c> est écrit directement ; s'il
/// ne peut pas l'être (clé USB en FAT32), le fichier reste sans marque.
/// </summary>
internal sealed partial class AttachmentSafety(ILogger<AttachmentSafety> logger) : IAttachmentSafety
{
    /// <summary>Quai Mail auprès du gestionnaire de pièces jointes (Windows y rattache ses propres choix).</summary>
    private static readonly Guid ClientGuid = new("0f2b58ea-cbcc-4421-8fc3-575034e6ea91");

    /// <summary>CLSID_AttachmentServices (shobjidl_core.h).</summary>
    private static readonly Guid AttachmentServices = new("4125dd96-e03a-4103-8f70-e0597d803b9c");

    private const uint ClsctxInprocServer = 0x1;

    private static readonly StrategyBasedComWrappers ComWrappers = new();

    /// <summary>Bloqué par la stratégie de sécurité de Windows.</summary>
    private const int InetESecurityProblem = unchecked((int)0x800C000E);

    /// <summary>Menace trouvée par l'antivirus (même lecture que Chromium).</summary>
    private const int EFail = unchecked((int)0x80004005);

    /// <summary>Le dernier fichier a été marqué par le gestionnaire de Windows lui-même (test de fumée).</summary>
    internal bool LastMarkedByWindows { get; private set; }

    public Task<SavedFileVerdict> MarkAsync(string path, CancellationToken cancellationToken = default)
    {
        // Objet COM « appartement » sur un thread STA à part : la fenêtre ne se fige pas pendant l'analyse antivirus.
        var done = new TaskCompletionSource<SavedFileVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.TrySetResult(Mark(path));
            }
            catch (Exception ex)
            {
                logger.LogWarning("Gestionnaire de pièces jointes de Windows en échec ({Error}), marque écrite directement", ex.GetType().Name);
                done.TrySetResult(WriteZoneIdentifier(path));
            }
        })
        {
            IsBackground = true,
            Name = "Marque du web",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private SavedFileVerdict Mark(string path)
    {
        LastMarkedByWindows = false;
        var clsid = AttachmentServices;
        var iid = typeof(IAttachmentExecute).GUID;
        var created = CoCreateInstance(in clsid, IntPtr.Zero, ClsctxInprocServer, in iid, out var pointer);
        if (created < 0 || pointer == IntPtr.Zero)
        {
            logger.LogWarning("Gestionnaire de pièces jointes de Windows indisponible (0x{Code:X8}), marque écrite directement", created);
            return WriteZoneIdentifier(path);
        }

        var execute = (IAttachmentExecute)ComWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        Marshal.Release(pointer);
        int saved;
        try
        {
            execute.SetClientGuid(in ClientGuid);
            execute.SetLocalPath(path);

            // Une pièce jointe n'a pas d'adresse d'origine : « about:internet » la range dans la zone Internet.
            execute.SetSource("about:internet");
            saved = execute.Save();
        }
        finally
        {
            ((ComObject)(object)execute).FinalRelease();
        }

        switch (saved)
        {
            case 0:
                LastMarkedByWindows = true;
                return File.Exists(path) ? SavedFileVerdict.Marked : SavedFileVerdict.Blocked;
            case InetESecurityProblem:
            case EFail:
                return SavedFileVerdict.Blocked;
            default:
                logger.LogWarning("Marque du web refusée par Windows (0x{Code:X8}), écrite directement", saved);
                return WriteZoneIdentifier(path);
        }
    }

    /// <summary>Repli : flux NTFS <c>Zone.Identifier</c>, zone 3 (Internet), comme l'écrivent les navigateurs.</summary>
    private SavedFileVerdict WriteZoneIdentifier(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet\r\n", Encoding.ASCII);
            return SavedFileVerdict.Marked;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            logger.LogWarning("Marque du web impossible sur ce volume ({Error})", ex.GetType().Name);
            return SavedFileVerdict.Unmarked;
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    /// <summary>IAttachmentExecute (shobjidl_core.h), méthodes dans l'ordre de la table virtuelle.</summary>
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("73db1241-1e85-4581-8e4f-a81e1d0f8c57")]
    internal partial interface IAttachmentExecute
    {
        void SetClientTitle(string title);

        void SetClientGuid(in Guid guid);

        void SetLocalPath(string localPath);

        void SetFileName(string fileName);

        void SetSource(string source);

        void SetReferrer(string referrer);

        [PreserveSig]
        int CheckPolicy();

        [PreserveSig]
        int Prompt(IntPtr window, int prompt, out int action);

        [PreserveSig]
        int Save();

        [PreserveSig]
        int Execute(IntPtr window, string? verb, out IntPtr process);

        [PreserveSig]
        int SaveWithUI(IntPtr window);

        [PreserveSig]
        int ClearClientState();
    }
}
