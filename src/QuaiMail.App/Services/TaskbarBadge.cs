using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Logging;
using QuaiMail.App.Localization;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.App.Services;

/// <summary>
/// Pastille des non lus sur l'icône de Quai Mail dans la barre des tâches, comme Discord : rond rouge, nombre blanc
/// (« 9+ » au-delà de 9). Posée par Windows lui-même (<c>ITaskbarList3::SetOverlayIcon</c>, l'icône superposée des
/// applications Win32, sans identité de paquet), dessinée par GDI+ à la taille des petites icônes pour l'échelle de
/// l'écran. Doit être appelée depuis le thread de la fenêtre (STA).
/// </summary>
internal sealed partial class TaskbarBadge(ILogger<TaskbarBadge> logger)
{
    /// <summary>CLSID_TaskbarList (shobjidl_core.h).</summary>
    private static readonly Guid TaskbarListClsid = new("56fdf344-fd6d-11d0-958a-006097c9a090");

    private const uint ClsctxInprocServer = 0x1;

    /// <summary>Rouge de la pastille (comme Discord) et texte blanc, en ARGB.</summary>
    private const uint BadgeColor = 0xFFF23F43;
    private const uint TextColor = 0xFFFFFFFF;

    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private ITaskbarList3? _taskbar;
    private IntPtr _window;
    private bool _initialized;

    /// <summary>Texte affiché (<c>null</c> : pas de pastille) ; test de fumée.</summary>
    internal string? LastText { get; private set; }

    /// <summary>La barre des tâches de Windows a répondu (sinon, aucune pastille possible, sans erreur).</summary>
    internal bool TaskbarAvailable => _taskbar is not null;

    /// <summary>Dernier code de <c>SetOverlayIcon</c> (test de fumée).</summary>
    internal int LastResult { get; private set; }

    /// <summary>Taille en pixels de la dernière pastille dessinée (0 : aucune ou dessin impossible).</summary>
    internal int LastIconSize { get; private set; }

    /// <summary>Fenêtre dont le bouton de la barre des tâches porte la pastille.</summary>
    public void Attach(IntPtr window) => _window = window;

    /// <summary>Met la pastille à jour (rien ne change si le texte est le même, sauf <paramref name="force"/>).</summary>
    public void Show(int unread, bool enabled, bool force = false)
    {
        var text = UnreadBadge.Text(unread, enabled);
        if (_window == IntPtr.Zero || (!force && text == LastText && _initialized))
        {
            return;
        }

        if (Taskbar() is not { } taskbar)
        {
            LastText = text;
            return;
        }

        var icon = IntPtr.Zero;
        try
        {
            string? description = null;
            if (text is not null)
            {
                var size = Math.Max(16, NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, NativeMethods.GetDpiForWindow(_window)));
                icon = BadgeIcon.Render(text, size, BadgeColor, TextColor);
                LastIconSize = icon == IntPtr.Zero ? 0 : size;
                description = unread == 1 ? Loc.Get("Taskbar_UnreadOne") : Loc.Format("Taskbar_UnreadMany", unread);
            }

            if (text is not null && icon == IntPtr.Zero)
            {
                logger.LogWarning("Pastille des non lus : dessin impossible");
                return;
            }

            LastResult = taskbar.SetOverlayIcon(_window, icon, description);
            if (LastResult < 0)
            {
                logger.LogWarning("Pastille des non lus refusée par la barre des tâches (0x{Code:X8})", LastResult);
            }

            LastText = text;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Pastille des non lus indisponible ({Error})", ex.GetType().Name);
        }
        finally
        {
            // La barre des tâches garde sa propre copie de l'icône.
            if (icon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(icon);
            }
        }
    }

    private ITaskbarList3? Taskbar()
    {
        if (_initialized)
        {
            return _taskbar;
        }

        _initialized = true;
        try
        {
            var clsid = TaskbarListClsid;
            var iid = typeof(ITaskbarList3).GUID;
            var created = CoCreateInstance(in clsid, IntPtr.Zero, ClsctxInprocServer, in iid, out var pointer);
            if (created < 0 || pointer == IntPtr.Zero)
            {
                logger.LogWarning("Barre des tâches indisponible (0x{Code:X8}) : pas de pastille des non lus", created);
                return null;
            }

            var taskbar = (ITaskbarList3)ComWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
            Marshal.Release(pointer);
            var init = taskbar.HrInit();
            if (init < 0)
            {
                logger.LogWarning("Barre des tâches indisponible (0x{Code:X8}) : pas de pastille des non lus", init);
                return null;
            }

            _taskbar = taskbar;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Barre des tâches indisponible ({Error}) : pas de pastille des non lus", ex.GetType().Name);
        }

        return _taskbar;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    /// <summary>
    /// ITaskbarList3 (shobjidl_core.h), avec les méthodes d'ITaskbarList et ITaskbarList2 dont il hérite, dans l'ordre de
    /// la table virtuelle. Seules <c>HrInit</c> et <c>SetOverlayIcon</c> servent ; les autres gardent leur place.
    /// </summary>
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    internal partial interface ITaskbarList3
    {
        // ITaskbarList
        [PreserveSig]
        int HrInit();

        [PreserveSig]
        int AddTab(IntPtr window);

        [PreserveSig]
        int DeleteTab(IntPtr window);

        [PreserveSig]
        int ActivateTab(IntPtr window);

        [PreserveSig]
        int SetActiveAlt(IntPtr window);

        // ITaskbarList2
        [PreserveSig]
        int MarkFullscreenWindow(IntPtr window, int fullscreen);

        // ITaskbarList3
        [PreserveSig]
        int SetProgressValue(IntPtr window, ulong completed, ulong total);

        [PreserveSig]
        int SetProgressState(IntPtr window, int flags);

        [PreserveSig]
        int RegisterTab(IntPtr tab, IntPtr mdi);

        [PreserveSig]
        int UnregisterTab(IntPtr tab);

        [PreserveSig]
        int SetTabOrder(IntPtr tab, IntPtr insertBefore);

        [PreserveSig]
        int SetTabActive(IntPtr tab, IntPtr mdi, uint reserved);

        [PreserveSig]
        int ThumbBarAddButtons(IntPtr window, uint count, IntPtr buttons);

        [PreserveSig]
        int ThumbBarUpdateButtons(IntPtr window, uint count, IntPtr buttons);

        [PreserveSig]
        int ThumbBarSetImageList(IntPtr window, IntPtr imageList);

        [PreserveSig]
        int SetOverlayIcon(IntPtr window, IntPtr icon, string? description);

        [PreserveSig]
        int SetThumbnailTooltip(IntPtr window, string? tip);

        [PreserveSig]
        int SetThumbnailClip(IntPtr window, IntPtr clip);
    }
}

/// <summary>
/// Dessin de la pastille par GDI+ (fourni par Windows) : rond plein, texte en chemin vectoriel centré sur ses vrais
/// contours (les chiffres restent au milieu quelle que soit la police), lissé, sur fond transparent.
/// </summary>
internal static partial class BadgeIcon
{
    private const int PixelFormat32bppArgb = 0x0026200A;
    private const int SmoothingModeAntiAlias = 4;
    private const int PixelOffsetModeHalf = 4;
    private const int FillModeWinding = 1;
    private const int FontStyleBold = 1;
    private const int MatrixOrderAppend = 1;

    private static readonly Lazy<bool> Started = new(Startup);

    /// <summary>Icône (HICON, à détruire par l'appelant) ou <see cref="IntPtr.Zero"/> si GDI+ échoue.</summary>
    public static IntPtr Render(string text, int size, uint background, uint foreground)
    {
        if (!Started.Value)
        {
            return IntPtr.Zero;
        }

        IntPtr bitmap = 0, graphics = 0, circle = 0, ink = 0, family = 0, path = 0;
        try
        {
            if (GdipCreateBitmapFromScan0(size, size, 0, PixelFormat32bppArgb, 0, out bitmap) != 0
                || GdipGetImageGraphicsContext(bitmap, out graphics) != 0)
            {
                return IntPtr.Zero;
            }

            GdipSetSmoothingMode(graphics, SmoothingModeAntiAlias);
            GdipSetPixelOffsetMode(graphics, PixelOffsetModeHalf);
            GdipGraphicsClear(graphics, 0);

            GdipCreateSolidFill(background, out circle);
            GdipFillEllipse(graphics, circle, 0, 0, size, size);

            if (GdipCreateFontFamilyFromName("Segoe UI", 0, out family) != 0)
            {
                family = 0;
                GdipGetGenericFontFamilySansSerif(out family);
            }

            var layout = new RectF(0, 0, size * 4, size * 4);
            if (family == 0
                || GdipCreatePath(FillModeWinding, out path) != 0
                || GdipAddPathString(path, text, text.Length, family, FontStyleBold, size, in layout, 0) != 0
                || GdipGetPathWorldBounds(path, out var bounds, 0, 0) != 0
                || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return IntPtr.Zero;
            }

            // Chiffres à mi-hauteur du rond, « 9+ » rétréci s'il déborde en largeur.
            var scale = Math.Min(size * 0.5f / bounds.Height, size * 0.66f / bounds.Width);
            GdipTranslateWorldTransform(graphics, -(bounds.X + bounds.Width / 2), -(bounds.Y + bounds.Height / 2), MatrixOrderAppend);
            GdipScaleWorldTransform(graphics, scale, scale, MatrixOrderAppend);
            GdipTranslateWorldTransform(graphics, size / 2f, size / 2f, MatrixOrderAppend);
            GdipCreateSolidFill(foreground, out ink);
            GdipFillPath(graphics, ink, path);

            return GdipCreateHICONFromBitmap(bitmap, out var icon) == 0 ? icon : IntPtr.Zero;
        }
        finally
        {
            if (path != 0)
            {
                GdipDeletePath(path);
            }

            if (family != 0)
            {
                GdipDeleteFontFamily(family);
            }

            if (ink != 0)
            {
                GdipDeleteBrush(ink);
            }

            if (circle != 0)
            {
                GdipDeleteBrush(circle);
            }

            if (graphics != 0)
            {
                GdipDeleteGraphics(graphics);
            }

            if (bitmap != 0)
            {
                GdipDisposeImage(bitmap);
            }
        }
    }

    /// <summary>GDI+ démarré une fois pour toute la vie du processus.</summary>
    private static bool Startup()
    {
        var input = new StartupInput { GdiplusVersion = 1 };
        return GdiplusStartup(out _, in input, 0) == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RectF(float x, float y, float width, float height)
    {
        public readonly float X = x;
        public readonly float Y = y;
        public readonly float Width = width;
        public readonly float Height = height;
    }

    [LibraryImport("gdiplus.dll")]
    private static partial int GdiplusStartup(out nint token, in StartupInput input, nint output);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, nint scan0, out nint bitmap);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipGetImageGraphicsContext(nint image, out nint graphics);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipSetSmoothingMode(nint graphics, int mode);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipSetPixelOffsetMode(nint graphics, int mode);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipGraphicsClear(nint graphics, uint color);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipCreateSolidFill(uint color, out nint brush);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipFillEllipse(nint graphics, nint brush, float x, float y, float width, float height);

    [LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GdipCreateFontFamilyFromName(string name, nint collection, out nint family);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipGetGenericFontFamilySansSerif(out nint family);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipCreatePath(int fillMode, out nint path);

    [LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GdipAddPathString(nint path, string text, int length, nint family, int style, float emSize, in RectF layout, nint format);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipGetPathWorldBounds(nint path, out RectF bounds, nint matrix, nint pen);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipTranslateWorldTransform(nint graphics, float dx, float dy, int order);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipScaleWorldTransform(nint graphics, float sx, float sy, int order);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipFillPath(nint graphics, nint brush, nint path);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipCreateHICONFromBitmap(nint bitmap, out nint icon);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipDeletePath(nint path);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipDeleteFontFamily(nint family);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipDeleteBrush(nint brush);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipDeleteGraphics(nint graphics);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipDisposeImage(nint image);
}
