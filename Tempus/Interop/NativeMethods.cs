using System.Runtime.InteropServices;

namespace Tempus.Interop;

/// <summary>
/// P/Invoke bruto. Nada de política aqui — quem decide é <see cref="TaskbarInfo"/>
/// e <c>BarPlacement</c>.
/// </summary>
internal static class NativeMethods
{
    // ---- Estilos de janela ----
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x0000_0080; // fora do Alt+Tab
    public const int WS_EX_NOACTIVATE = 0x0800_0000; // nunca rouba foco

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    // ---- Mensagens ----
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_SETTINGCHANGE = 0x001A;

    // ---- WinEvents ----
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // ---- AppBar (só para consultar estado da taskbar, nunca para registrar uma) ----
    public const uint ABM_GETSTATE = 0x0000_0004;
    public const int ABS_AUTOHIDE = 0x0000_0001;

    // ---- Monitor ----
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool IsEmpty => Right <= Left || Bottom <= Top;

        public static RECT Intersect(RECT a, RECT b) => new()
        {
            Left = Math.Max(a.Left, b.Left),
            Top = Math.Max(a.Top, b.Top),
            Right = Math.Min(a.Right, b.Right),
            Bottom = Math.Min(a.Bottom, b.Bottom),
        };

        public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>Estado de notificação do usuário — usado para esconder a barra em tela cheia.</summary>
    public enum UserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3dFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
        string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shell32.dll", SetLastError = true)]
    public static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out UserNotificationState state);

    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd,
        int objectId, int childId, uint threadId, uint timestamp);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hook);

    // GetWindowLongPtr/SetWindowLongPtr só existem em 64-bit; em 32-bit os nomes são sem Ptr.
    public static IntPtr GetWindowLongAuto(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    public static IntPtr SetWindowLongAuto(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : SetWindowLong32(hWnd, nIndex, dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

    /// <summary>
    /// Pinta a barra de título de escuro. Ela pertence ao Windows, não ao WPF, então uma janela de
    /// conteúdo escuro nasce com título claro se ninguém pedir o contrário.
    /// <para>
    /// Falha silenciosa por design: em build que não conheça o atributo a chamada devolve erro e
    /// nada acontece — barra de título clara é cosmético, não vale derrubar a janela por isso.
    /// </para>
    /// </summary>
    public static void UseDarkTitleBar(IntPtr handle, bool dark)
    {
        if (handle == IntPtr.Zero) return;

        var value = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SwRestore = 9;

    /// <summary>
    /// <c>a</c> está à frente de <c>b</c> na ordem Z? <c>EnumWindows</c> enumera de cima para
    /// baixo, então quem aparece primeiro está na frente.
    /// <para>
    /// Serve para reafirmar o topo <b>só quando necessário</b>. Reafirmar a cada segundo sem
    /// perguntar joga a barra para o topo da faixa topmost — acima dos próprios menus e dicas dela,
    /// que somem atrás da barra a cada rodada.
    /// </para>
    /// <para>
    /// Devolve <c>true</c> se <c>b</c> não for encontrado: sem taskbar não há atrás de quê ficar.
    /// </para>
    /// </summary>
    public static bool IsInFrontOf(IntPtr a, IntPtr b)
    {
        if (a == IntPtr.Zero || b == IntPtr.Zero) return true;

        var found = 0;
        var aFirst = false;

        EnumWindows((handle, _) =>
        {
            if (handle == a)
            {
                if (found == 0) aFirst = true;
                found++;
            }
            else if (handle == b)
            {
                found++;
            }

            return found < 2; // os dois já apareceram: o resto da ordem não interessa
        }, IntPtr.Zero);

        // Só um encontrado: o outro está oculto ou morto, e não há disputa.
        return found < 2 || aFirst;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    /// <summary>
    /// Traz uma janela para a frente. Existe porque a barra é <c>WS_EX_NOACTIVATE</c> (D-002) e
    /// diálogos abertos a partir dela herdam o não-foco — nascem atrás de tudo, e o usuário conclui
    /// que o clique no menu não funcionou.
    /// </summary>
    public static void BringToFront(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;

        _ = ShowWindow(handle, SwRestore);
        _ = SetForegroundWindow(handle);
    }
}
