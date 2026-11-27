using System.Runtime.InteropServices;
using QuaiMail.Core.Agent;
using static QuaiMail.Agent.NativeMethods;

namespace QuaiMail.Agent;

/// <summary>
/// Icône de l'agent dans la zone de notification (icônes cachées) : une fenêtre Win32 jamais affichée reçoit ses clics,
/// les commandes de son menu, le redémarrage de l'Explorateur (l'icône est remise), la sortie de veille et la fermeture
/// de session. Les notifications de nouveaux mails sont des bulles de l'icône (que Windows 10 et 11 montrent comme des
/// notifications et respectent le mode « Ne pas déranger »). Tout se passe sur le fil de la fenêtre ; les autres fils
/// passent par <see cref="SetStatus"/>, <see cref="ShowBalloon"/> et <see cref="Close"/>.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint CallbackMessage = WM_APP + 1;
    private const uint RefreshMessage = WM_APP + 2;
    private const uint IconId = 1;

    private static TrayIcon? s_current;

    private readonly Lock _gate = new();
    private readonly uint _taskbarCreated;
    private nint _window;
    private nint _smallIcon;
    private nint _largeIcon;
    private bool _added;
    private string _tip = AgentText.TipStarting;
    private string _status = AgentText.StatusStarting;
    private bool _tipChanged;
    private (string Title, string Text)? _balloon;

    public TrayIcon()
    {
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>Clic sur l'icône ou « Ouvrir Quai Mail ».</summary>
    public event Action? OpenRequested;

    /// <summary>Clic sur la dernière notification.</summary>
    public event Action? BalloonClicked;

    /// <summary>« Lancer avec Windows » coché ou décoché.</summary>
    public event Action? AutoStartToggled;

    /// <summary>Sortie de veille de l'ordinateur.</summary>
    public event Action? Resumed;

    /// <summary>État de la case « Lancer avec Windows », lu à chaque ouverture du menu.</summary>
    public Func<bool> IsAutoStartEnabled { get; set; } = () => false;

    public void Create()
    {
        s_current = this;
        var instance = GetModuleHandle(0);
        fixed (char* className = AgentInfo.WindowClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                hInstance = instance,
                lpszClassName = className,
            };
            if (RegisterClassEx(&windowClass) == 0)
            {
                throw new InvalidOperationException($"Classe de fenêtre refusée ({Marshal.GetLastPInvokeError()}).");
            }
        }

        // Fenêtre de premier niveau (jamais affichée) : seule une telle fenêtre reçoit « TaskbarCreated » et les messages
        // de fin de session envoyés par Windows et par l'installateur.
        _window = CreateWindowEx(0, AgentInfo.WindowClassName, AgentText.WindowTitle, 0, 0, 0, 0, 0, 0, 0, instance, 0);
        if (_window == 0)
        {
            throw new InvalidOperationException("Fenêtre de l'agent non créée.");
        }

        LoadIcons();
        Add();
    }

    /// <summary>Boucle de messages : rend la main quand la fenêtre est détruite (menu « Arrêter », <see cref="Close"/>).</summary>
    public void Run()
    {
        MSG message;
        while (GetMessage(&message, 0, 0, 0) > 0)
        {
            TranslateMessage(&message);
            DispatchMessage(&message);
        }
    }

    /// <summary>Infobulle de l'icône et première ligne du menu (depuis n'importe quel fil).</summary>
    public void SetStatus(string tip, string status)
    {
        lock (_gate)
        {
            _tip = tip;
            _status = status;
            _tipChanged = true;
        }

        PostMessage(_window, RefreshMessage, 0, 0);
    }

    /// <summary>Notification de nouveaux mails (depuis n'importe quel fil) ; seule la dernière compte.</summary>
    public void ShowBalloon(string title, string text)
    {
        lock (_gate)
        {
            _balloon = (title, text);
        }

        PostMessage(_window, RefreshMessage, 0, 0);
    }

    /// <summary>Ferme l'agent (depuis n'importe quel fil).</summary>
    public void Close()
    {
        if (_window != 0)
        {
            PostMessage(_window, WM_CLOSE, 0, 0);
        }
    }

    public void Dispose()
    {
        Remove();
        if (_window != 0)
        {
            DestroyWindow(_window);
            _window = 0;
        }

        foreach (var icon in new[] { _smallIcon, _largeIcon })
        {
            if (icon != 0)
            {
                DestroyIcon(icon);
            }
        }

        _smallIcon = _largeIcon = 0;
        s_current = null;
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (s_current is { } tray && tray.Handle(window, message, wParam, lParam) is { } handled)
            {
                return handled;
            }
        }
        catch (Exception)
        {
            // Une erreur dans un clic ne doit jamais fermer l'agent (ni remonter dans Windows).
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private nint? Handle(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            // L'Explorateur a redémarré : la zone de notification est neuve, l'icône doit être remise.
            _added = false;
            Add();
            return 0;
        }

        switch (message)
        {
            case CallbackMessage:
                switch ((uint)LowWord(lParam))
                {
                    case WM_CONTEXTMENU:
                        ShowMenu(window, SignedLowWord(wParam), SignedHighWord(wParam));
                        break;
                    case NIN_SELECT:
                    case NIN_KEYSELECT:
                        OpenRequested?.Invoke();
                        break;
                    case NIN_BALLOONUSERCLICK:
                        BalloonClicked?.Invoke();
                        break;
                }

                return 0;

            case WM_COMMAND:
                switch ((int)LowWord(wParam))
                {
                    case AgentInfo.CommandOpen:
                        OpenRequested?.Invoke();
                        break;
                    case AgentInfo.CommandToggleAutoStart:
                        AutoStartToggled?.Invoke();
                        break;
                    case AgentInfo.CommandQuit:
                        DestroyWindow(window);
                        break;
                }

                return 0;

            case RefreshMessage:
                Refresh();
                return 0;

            case WM_POWERBROADCAST:
                if (wParam is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND)
                {
                    Resumed?.Invoke();
                }

                return 1;

            case WM_QUERYENDSESSION:
                // Fermeture de session, ou installateur qui remplace les fichiers : l'agent n'a rien à garder.
                return 1;

            case WM_ENDSESSION:
                if (wParam != 0)
                {
                    DestroyWindow(window);
                }

                return 0;

            case WM_CLOSE:
                DestroyWindow(window);
                return 0;

            case WM_DESTROY:
                Remove();
                _window = 0;
                PostQuitMessage(0);
                return 0;

            default:
                return null;
        }
    }

    private void ShowMenu(nint window, int x, int y)
    {
        string status;
        lock (_gate)
        {
            status = _status;
        }

        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MF_STRING | MF_GRAYED, 0, status);
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, AgentInfo.CommandOpen, AgentText.Open);
            SetMenuDefaultItem(menu, AgentInfo.CommandOpen, 0);
            AppendMenu(menu, MF_STRING | (IsAutoStartEnabled() ? MF_CHECKED : 0), AgentInfo.CommandToggleAutoStart, AgentText.StartWithWindows);
            AppendMenu(menu, MF_STRING, AgentInfo.CommandQuit, AgentText.Quit);

            // Sans fenêtre au premier plan, le menu ne se refermerait pas en cliquant ailleurs (comportement connu de Windows).
            SetForegroundWindow(window);
            var align = GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
            TrackPopupMenuEx(menu, align | TPM_BOTTOMALIGN | TPM_RIGHTBUTTON, x, y, window, 0);
            PostMessage(window, WM_NULL, 0, 0);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void Add()
    {
        var data = Data();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _smallIcon;
        lock (_gate)
        {
            Copy(_tip, data.szTip, 128);
        }

        _added = Shell_NotifyIcon(NIM_ADD, &data);
        if (_added)
        {
            data.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, &data);
        }

        // Sinon (Explorateur pas encore prêt à l'ouverture de la session) : « TaskbarCreated » arrivera.
    }

    private void Remove()
    {
        if (_added && _window != 0)
        {
            var data = Data();
            Shell_NotifyIcon(NIM_DELETE, &data);
            _added = false;
        }
    }

    private void Refresh()
    {
        string? tip = null;
        (string Title, string Text)? balloon;
        lock (_gate)
        {
            if (_tipChanged)
            {
                tip = _tip;
                _tipChanged = false;
            }

            balloon = _balloon;
            _balloon = null;
        }

        if (!_added)
        {
            Add();
        }

        if (tip is not null)
        {
            var data = Data();
            data.uFlags = NIF_TIP | NIF_SHOWTIP;
            Copy(tip, data.szTip, 128);
            Shell_NotifyIcon(NIM_MODIFY, &data);
        }

        if (balloon is { } shown)
        {
            var data = Data();
            data.uFlags = NIF_INFO;
            Copy(shown.Title, data.szInfoTitle, 64);
            Copy(shown.Text, data.szInfo, 256);
            data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON | NIIF_RESPECT_QUIET_TIME;
            data.hBalloonIcon = _largeIcon != 0 ? _largeIcon : _smallIcon;
            Shell_NotifyIcon(NIM_MODIFY, &data);
        }
    }

    private NOTIFYICONDATAW Data() => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _window,
        uID = IconId,
    };

    /// <summary>Icône de Quai Mail (fichier livré à côté, sinon celle de l'exécutable), aux tailles de Windows.</summary>
    private void LoadIcons()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "QuaiMail.ico");
        if (File.Exists(file))
        {
            _smallIcon = LoadImage(0, file, IMAGE_ICON, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), LR_LOADFROMFILE);
            _largeIcon = LoadImage(0, file, IMAGE_ICON, GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CYICON), LR_LOADFROMFILE);
        }

        if (_smallIcon == 0 && Environment.ProcessPath is { } exe)
        {
            nint large = 0, small = 0;
            if (ExtractIconEx(exe, 0, &large, &small, 1) > 0)
            {
                _smallIcon = small;
                _largeIcon = _largeIcon != 0 ? _largeIcon : large;
            }
        }

        if (_smallIcon == 0)
        {
            _smallIcon = LoadIcon(0, IDI_APPLICATION);
        }
    }

    private static void Copy(string text, char* destination, int capacity)
    {
        var length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(new Span<char>(destination, capacity));
        destination[length] = '\0';
    }
}
