using static Tempus.Interop.NativeMethods;

namespace Tempus.Interop;

internal enum TaskbarEdge { Bottom, Top, Left, Right }

/// <summary>
/// Onde está a taskbar, agora. Consultado por coordenada, nunca reparentando.
/// <para>
/// D-002: <b>jamais</b> usar <c>SetParent</c> para entrar em <c>Shell_TrayWnd</c>. É o hack
/// que quebra em restart do explorer, troca de DPI e updates do Windows. Este tipo existe
/// justamente para tornar a alternativa correta — ler o retângulo e se posicionar ao lado —
/// tão fácil quanto o hack.
/// </para>
/// </summary>
internal sealed record TaskbarInfo(
    IntPtr Handle,
    RECT Bounds,
    RECT TrayBounds,
    RECT MonitorBounds,
    TaskbarEdge Edge,
    bool IsAutoHide,
    bool IsHidden)
{
    public bool IsHorizontal => Edge is TaskbarEdge.Bottom or TaskbarEdge.Top;

    /// <summary>Localiza a taskbar primária, ou <c>null</c> se ela não existe neste instante.</summary>
    /// <remarks>
    /// Retornar <c>null</c> é um resultado normal, não um erro: durante um restart do explorer
    /// a janela realmente não existe por alguns segundos. Quem chama deve esconder a barra e
    /// tentar de novo quando chegar <c>TaskbarCreated</c>.
    /// </remarks>
    public static TaskbarInfo? Locate()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !IsWindow(taskbar)) return null;
        if (!GetWindowRect(taskbar, out var bounds) || bounds.IsEmpty) return null;

        var monitor = MonitorFromWindow(taskbar, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;

        var edge = DetectEdge(bounds, info.rcMonitor);
        var isAutoHide = QueryAutoHide();
        var isHidden = isAutoHide && IsSlidOffScreen(bounds, info.rcMonitor, edge);

        // TrayNotifyWnd é a área de bandeja (relógio + ícones). Ancorar à esquerda dela
        // cai em espaço tipicamente vazio, tanto com ícones centralizados quanto à esquerda.
        var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        var trayBounds = bounds;
        if (tray != IntPtr.Zero && GetWindowRect(tray, out var tb) && !tb.IsEmpty)
            trayBounds = tb;

        return new TaskbarInfo(taskbar, bounds, trayBounds, info.rcMonitor, edge, isAutoHide, isHidden);
    }

    private static TaskbarEdge DetectEdge(RECT taskbar, RECT monitor)
    {
        var horizontal = taskbar.Width >= taskbar.Height;
        if (horizontal)
            return taskbar.Top - monitor.Top <= monitor.Bottom - taskbar.Bottom
                ? TaskbarEdge.Top
                : TaskbarEdge.Bottom;

        return taskbar.Left - monitor.Left <= monitor.Right - taskbar.Right
            ? TaskbarEdge.Left
            : TaskbarEdge.Right;
    }

    private static bool QueryAutoHide()
    {
        var data = new APPBARDATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>() };
        var state = SHAppBarMessage(ABM_GETSTATE, ref data).ToInt64();
        return (state & ABS_AUTOHIDE) != 0;
    }

    /// <summary>
    /// Com auto-hide ligado, a taskbar escondida continua existindo — ela só desliza para fora
    /// da tela, deixando uma faixa de poucos pixels. Detectamos isso pela interseção com o
    /// monitor, em vez de tentar ler um estado de animação que o shell não expõe.
    /// </summary>
    private static bool IsSlidOffScreen(RECT taskbar, RECT monitor, TaskbarEdge edge)
    {
        var visible = RECT.Intersect(taskbar, monitor);
        if (visible.IsEmpty) return true;

        return edge is TaskbarEdge.Bottom or TaskbarEdge.Top
            ? visible.Height * 2 < taskbar.Height
            : visible.Width * 2 < taskbar.Width;
    }
}

internal static class ForegroundState
{
    /// <summary>
    /// True quando há app em tela cheia, jogo em D3D exclusivo, ou apresentação em curso —
    /// situações em que a barra deve sair da frente. Critério de aceite 2 do SPEC.
    /// </summary>
    public static bool ShouldYieldScreen()
    {
        if (SHQueryUserNotificationState(out var state) != 0) return false;

        return state is UserNotificationState.PresentationMode
            or UserNotificationState.RunningD3dFullScreen
            or UserNotificationState.Busy;
    }
}
