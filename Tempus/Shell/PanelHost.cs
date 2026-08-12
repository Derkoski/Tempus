using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Tempus.Interop;
using static Tempus.Interop.NativeMethods;

namespace Tempus.Shell;

/// <summary>
/// Comportamento comum dos painéis S2/S3: posicionar acima da barra, sair do Alt+Tab, e fechar
/// ao perder foco ou com <c>Esc</c>.
/// <para>
/// Composição em vez de herança — uma classe base com XAML exigiria mapear o namespace no
/// elemento raiz de cada painel, e são só dois. Aqui cada painel instancia um host e delega.
/// </para>
/// <para>
/// Diferença importante em relação à barra: painéis <b>não</b> levam
/// <c>WS_EX_NOACTIVATE</c>. Eles precisam de foco de teclado, porque o campo de nova tarefa
/// exige digitação. A barra é o oposto: nunca deve roubar foco.
/// </para>
/// </summary>
internal sealed class PanelHost
{
    private const int GapAboveBarDip = 6;

    private readonly Window _window;
    private IntPtr _handle;

    public PanelHost(Window window)
    {
        _window = window;

        _window.WindowStyle = WindowStyle.None;
        _window.ResizeMode = ResizeMode.NoResize;
        _window.ShowInTaskbar = false;
        _window.Topmost = true;
        _window.SizeToContent = SizeToContent.Height;

        // Necessário para os cantos arredondados e para Window.Opacity funcionar. Custa
        // renderização por software, aceitável porque painéis são transitórios e pequenos —
        // a barra, que fica aberta o dia todo, não usa transparência.
        _window.AllowsTransparency = true;
        _window.Opacity = 0; // evita o flash de aparecer no lugar errado antes de posicionar

        _window.SourceInitialized += OnSourceInitialized;
        _window.ContentRendered += OnContentRendered;
        _window.Deactivated += (_, _) => Close();
        _window.PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Retângulo da barra, em pixels físicos, usado como âncora.</summary>
    public RECT? Anchor { get; set; }

    public event EventHandler? CloseRequested;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(_window).Handle;

        var exStyle = GetWindowLongAuto(_handle, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_TOOLWINDOW; // fora do Alt+Tab, mas ativável
        SetWindowLongAuto(_handle, GWL_EXSTYLE, new IntPtr(exStyle));
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        Reposition();
        _window.Opacity = 1;
        _window.Activate();
    }

    /// <summary>Abre (ou reposiciona) o painel ancorado na barra.</summary>
    public void ShowAt(RECT anchor)
    {
        Anchor = anchor;

        if (!_window.IsVisible) _window.Show();

        Reposition();
        _window.Activate();
    }

    /// <summary>
    /// Posiciona por <c>SetWindowPos</c> com <c>SWP_NOSIZE</c>: o tamanho continua sendo do WPF
    /// (<c>SizeToContent</c>), e nós só escolhemos onde. Impor tamanho aqui brigaria com o layout.
    /// </summary>
    public void Reposition()
    {
        if (_handle == IntPtr.Zero || Anchor is not { } anchor) return;
        if (!GetWindowRect(_handle, out var self) || self.IsEmpty) return;

        var dpi = GetDpiForWindow(_handle);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        var gap = (int)Math.Round(GapAboveBarDip * scale);

        // Alinhado à direita da barra e imediatamente acima dela.
        var left = anchor.Right - self.Width;
        var top = anchor.Top - gap - self.Height;

        // Não escapar do monitor onde a barra está.
        var monitor = MonitorFromWindow(_handle, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            left = Math.Max(info.rcMonitor.Left, Math.Min(left, info.rcMonitor.Right - self.Width));
            top = Math.Max(info.rcMonitor.Top, top);
        }

        SetWindowPos(_handle, HWND_TOPMOST, left, top, 0, 0,
            SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        e.Handled = true;
        Close();
    }

    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
