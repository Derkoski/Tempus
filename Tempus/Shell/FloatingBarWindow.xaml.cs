using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Tempus.Domain;
using Tempus.Interop;
using static Tempus.Interop.NativeMethods;

namespace Tempus.Shell;

/// <summary>
/// A barra. Uma janela sem borda, sem foco e fora do Alt+Tab, posicionada em pixels físicos
/// sobre uma região vazia da taskbar.
/// <para>
/// D-002: posicionamento por coordenada. <b>Nunca</b> <c>SetParent</c> em <c>Shell_TrayWnd</c>.
/// </para>
/// </summary>
internal partial class FloatingBarWindow : Window
{
    private const int SanityCheckEveryTicks = 2; // timer de 1s → verificação a cada 2s

    private readonly BarOptions _options;
    private readonly SolidColorBrush _chipBrush = new(Colors.Transparent);
    private readonly DispatcherTimer _timer;

    private IntPtr _handle;
    private HwndSource? _source;
    private uint _taskbarCreatedMessage;
    private Palette _palette = Palette.FromSystemTheme();
    private ShellState _state = ShellState.Starting;
    private RECT? _appliedRect;
    private bool _isBarVisible;
    private bool _isBlinking;
    private int _tick;
    private bool _disposed;

    public FloatingBarWindow(BarOptions options)
    {
        _options = options;
        InitializeComponent();

        ReasonChip.Background = _chipBrush;

        // Tamanho inicial em DIP só para o WPF medir o conteúdo; a posição e o tamanho reais
        // são impostos por SetWindowPos em pixels físicos.
        Width = _options.Slots * _options.SlotWidthDip;
        Height = 40;

        ReasonChip.MouseLeftButtonUp += (_, e) => { e.Handled = true; OnReasonClicked(); };
        TimeArea.MouseLeftButtonUp += (_, e) => { e.Handled = true; OnTimeClicked(); };
        TasksArea.MouseLeftButtonUp += (_, e) => { e.Handled = true; Raise(TasksRequested); };
        MailArea.MouseLeftButtonUp += (_, e) => { e.Handled = true; Raise(MailRequested); };
        MouseRightButtonUp += (_, e) => { e.Handled = true; ShowContextMenu(); };

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += OnTick;

        SystemEvents.DisplaySettingsChanged += OnSystemChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Retângulo atual em pixels físicos, ou <c>null</c> se a barra está escondida.
    /// Os painéis usam isto como âncora.</summary>
    public RECT? CurrentRect => _appliedRect;

    public Palette CurrentPalette => _palette;

    public event EventHandler? Acknowledged;
    public event EventHandler? ReauthRequested;
    public event EventHandler? TasksRequested;
    public event EventHandler? AgendaRequested;
    public event EventHandler? MailRequested;
    public event EventHandler? SyncRequested;
    public event EventHandler? ExitRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _handle = new WindowInteropHelper(this).Handle;

        // Fora do Alt+Tab e sem roubar foco — critério de aceite 3 do SPEC.
        var exStyle = GetWindowLongAuto(_handle, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongAuto(_handle, GWL_EXSTYLE, new IntPtr(exStyle));

        // Broadcast quando o explorer reinicia. Sem tratar isto, a barra fica órfã
        // apontando para um retângulo que não existe mais.
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);

        ApplyPalette();
        Reposition();
        _timer.Start();
    }

    // ---------------------------------------------------------------- render

    public void Render(ShellState state)
    {
        _state = state;

        var (chipBackground, chipForeground) = _palette.For(state);
        var showChip = state.IsOffline || state.Severity > Severity.Calm;

        StopBlink();
        _chipBrush.Color = showChip ? chipBackground : Colors.Transparent;

        ReasonText.Text = state.Reason;
        ReasonText.Foreground = new SolidColorBrush(
            showChip ? chipForeground : _palette.BarForeground);
        ReasonChip.Visibility = string.IsNullOrEmpty(state.Reason)
            ? Visibility.Hidden
            : Visibility.Visible;

        // §1.1: nível 3 não reconhecido por 5 min passa a piscar âmbar↔vermelho.
        // Se o usuário desligou animações no Windows, fica vermelho sólido (invariante I8).
        if (state is { IsEscalated: true, Severity: Severity.Critical, IsOffline: false }
            && SystemParameters.ClientAreaAnimation)
        {
            StartBlink(_palette.CriticalBackground, _palette.AttentionBackground);
        }

        TasksCount.Text = FormatCount(state.OpenTasks);
        MailCount.Text = FormatCount(state.UnreadMail);

        // Contadores nunca herdam a cor do alerta (invariante I6).
        var counterBrush = new SolidColorBrush(
            state.IsOffline ? _palette.OfflineForeground : _palette.BarForeground);
        var iconBrush = new SolidColorBrush(
            state.IsOffline ? _palette.OfflineForeground : _palette.Muted);

        TasksCount.Foreground = counterBrush;
        MailCount.Foreground = counterBrush;
        TasksIcon.Foreground = iconBrush;
        MailIcon.Foreground = iconBrush;

        RenderTime(state);
        RenderBoundary(state);

        Background = new SolidColorBrush(
            state.IsOffline ? _palette.OfflineBackground : _palette.BarBackground);

        ReasonChip.ToolTip = BuildTooltip(state);
    }

    /// <summary>
    /// O slot esquerdo: a situação temporal. Tem vocabulário de cor próprio, independente da
    /// severidade do alarme (D-012), porque responde outra pergunta — "como está meu tempo agora"
    /// em vez de "algo precisa de mim".
    /// </summary>
    private void RenderTime(ShellState state)
    {
        var time = state.IsOffline ? TimeStatus.Unknown : state.Time;
        var (background, foreground) = _palette.For(time);

        TimeText.Text = time.Text;
        TimeText.Foreground = new SolidColorBrush(foreground);
        TimeText.FontWeight = time.IsBold ? FontWeights.SemiBold : FontWeights.Normal;

        // Preenchimento só nos humores que pedem antecipação; os calmos ficam com texto tingido.
        TimeArea.Background = new SolidColorBrush(background);

        TimeArea.ToolTip = state.IsOffline
            ? "Sem sincronização — não sei o que vem a seguir"
            : $"{time.Detail ?? time.Text}{Environment.NewLine}Clique para abrir a agenda";
    }

    /// <summary>
    /// A contagem regressiva do expediente. Fora dos dois vocabulários de cor: é um gradiente
    /// contínuo, não uma escala de estados, e some por completo fora da janela de aviso.
    /// </summary>
    private void RenderBoundary(ShellState state)
    {
        // Offline não sabe que horas o dia acaba para ninguém.
        var boundary = state.IsOffline ? null : state.Boundary;

        if (boundary is null)
        {
            BoundaryArea.Visibility = Visibility.Collapsed;
            return;
        }

        var (background, foreground) = Palette.Boundary(boundary.Progress);

        BoundaryArea.Visibility = Visibility.Visible;
        BoundaryArea.Background = new SolidColorBrush(background);
        BoundaryText.Text = boundary.Minutes.ToString();
        BoundaryText.Foreground = new SolidColorBrush(foreground);

        BoundaryArea.ToolTip =
            $"Faltam {boundary.Minutes} min para {boundary.At}{Environment.NewLine}Fim do expediente";
    }

    /// <summary>Contadores de 3+ dígitos viram <c>99+</c>; ausência de dado vira <c>—</c>.</summary>
    private static string FormatCount(int? value) => value switch
    {
        null => "—",
        > 99 => "99+",
        _ => value.Value.ToString(),
    };

    private static string BuildTooltip(ShellState state)
    {
        var lines = new List<string>();

        if (!string.IsNullOrEmpty(state.Reason)) lines.Add(state.Reason);

        lines.Add(state.LastSyncAt is { } sync
            ? $"Última sincronização: {sync.ToLocalTime():HH:mm:ss}"
            : "Nunca sincronizado");

        if (state.IsOffline) lines.Add("Clique para reconectar");
        else if (state.CanAcknowledge) lines.Add("Clique para reconhecer");

        return string.Join(Environment.NewLine, lines);
    }

    private void ApplyPalette()
    {
        _palette = Palette.FromSystemTheme();
        Render(_state);
    }

    private void StartBlink(Color from, Color to)
    {
        var animation = new ColorAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(600), // ida+volta = 1,2s de período
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };

        _chipBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        _isBlinking = true;
    }

    private void StopBlink()
    {
        if (!_isBlinking) return;

        _chipBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _isBlinking = false;
    }

    // ---------------------------------------------------------------- posicionamento

    /// <summary>
    /// Recalcula onde a barra deve estar e aplica. Idempotente e barato: só chama
    /// <c>SetWindowPos</c> quando o retângulo mudou de fato.
    /// </summary>
    private void Reposition()
    {
        if (_handle == IntPtr.Zero) return;

        var taskbar = TaskbarInfo.Locate();

        // Sair da frente: explorer reiniciando, taskbar com auto-hide recolhida, jogo em tela
        // cheia ou apresentação em curso. Critério de aceite 2 do SPEC.
        if (taskbar is null || taskbar.IsHidden || ForegroundState.ShouldYieldScreen())
        {
            HideBar();
            return;
        }

        var dpi = GetDpiForWindow(_handle);
        var target = BarPlacement.Compute(taskbar, _options, dpi);
        if (target is null)
        {
            HideBar();
            return;
        }

        var rect = target.Value;
        if (_isBarVisible && _appliedRect is { } applied && RectEquals(applied, rect))
        {
            AssertTopMost();
            return;
        }

        SetWindowPos(_handle, HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        _appliedRect = rect;
        _isBarVisible = true;
    }

    /// <summary>
    /// Reafirma a posição no topo da ordem Z.
    /// <para>
    /// A taskbar também é <c>HWND_TOPMOST</c>, e o explorer a reposiciona por conta própria — o
    /// que empurra a barra para <b>trás</b> dela. Quando isso acontece a barra não só desaparece:
    /// ela para de receber cliques, porque os pixels passam a pertencer ao <c>Shell_TrayWnd</c>.
    /// </para>
    /// <para>
    /// Não há evento para isso, então é reafirmação periódica. É a fragilidade estrutural que
    /// D-002 aceitou ao escolher a barra flutuante em vez dos ícones de bandeja.
    /// </para>
    /// </summary>
    private void AssertTopMost()
    {
        if (!_isBarVisible || _handle == IntPtr.Zero) return;

        SetWindowPos(_handle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void HideBar()
    {
        if (!_isBarVisible) return;

        ShowWindow(_handle, SW_HIDE);
        _appliedRect = null;
        _isBarVisible = false;
    }

    private static bool RectEquals(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private void OnTick(object? sender, EventArgs e)
    {
        // A cada segundo, e não a cada 2: perder a ordem Z deixa a barra invisível E surda a
        // cliques, então é o pior modo de falha da superfície. Uma chamada de SetWindowPos.
        AssertTopMost();

        // Verificação de sanidade a cada 2s. A maioria das mudanças chega por mensagem
        // (TaskbarCreated, DPI, display); isto cobre o que o shell não notifica, como a taskbar
        // com auto-hide recolhendo e a entrada em tela cheia.
        if (++_tick % SanityCheckEveryTicks == 0) Reposition();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
        {
            // O explorer reiniciou: a taskbar antiga não existe mais. Reposicionar do zero.
            _appliedRect = null;
            Reposition();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WM_DPICHANGED:
            case WM_DISPLAYCHANGE:
                _appliedRect = null;
                Reposition();
                break;

            case WM_SETTINGCHANGE:
                ApplyPalette();
                _appliedRect = null;
                Reposition();
                break;
        }

        return IntPtr.Zero;
    }

    private void OnSystemChanged(object? sender, EventArgs e)
    {
        _appliedRect = null;
        Reposition();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle)
        {
            ApplyPalette();
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        // Desbloqueio de sessão recria parte do shell; reposicionar por garantia.
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon
            or SessionSwitchReason.RemoteConnect)
        {
            _appliedRect = null;
            Reposition();
        }
    }

    // ---------------------------------------------------------------- interação

    private void OnReasonClicked()
    {
        if (_state.IsOffline)
        {
            Raise(ReauthRequested);
            return;
        }

        // O clique é o gesto central do produto: reconhecer tem prioridade sobre navegar,
        // porque é o que faz o alerta parar (D-006, invariante I3).
        if (_state.CanAcknowledge) Raise(Acknowledged);
        else Raise(AgendaRequested);
    }

    /// <summary>
    /// Clicar na situação temporal abre a agenda — nunca reconhece alerta. Reconhecer é exclusivo
    /// da área de motivo, para que o gesto tenha um lugar só e previsível.
    /// </summary>
    private void OnTimeClicked() => Raise(_state.IsOffline ? ReauthRequested : AgendaRequested);

    private void ShowContextMenu()
    {
        var menu = new ContextMenu { PlacementTarget = this };

        menu.Items.Add(MenuItemFor("Sincronizar agora", SyncRequested));
        menu.Items.Add(MenuItemFor("Abrir agenda", AgendaRequested));
        menu.Items.Add(MenuItemFor("Abrir tarefas", TasksRequested));

        if (_state.CanAcknowledge)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor("Reconhecer alerta", Acknowledged));
        }

        // Sempre disponível, e não só no estado Offline: mudar de escopo exige um novo consent
        // mesmo com o login válido, e sem esta entrada não haveria como pedir isso.
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Reconectar ao Google", ReauthRequested));

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Sair", ExitRequested));

        menu.IsOpen = true;
    }

    private MenuItem MenuItemFor(string header, EventHandler? handler)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => Raise(handler);
        return item;
    }

    private void Raise(EventHandler? handler) => handler?.Invoke(this, EventArgs.Empty);

    // ---------------------------------------------------------------- teardown

    public void Teardown()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        _timer.Tick -= OnTick;
        StopBlink();

        SystemEvents.DisplaySettingsChanged -= OnSystemChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        _source?.RemoveHook(WndProc);
        _source = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        Teardown();
        base.OnClosed(e);
    }
}
