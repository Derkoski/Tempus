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

    /// <summary>Quantas reafirmações a rajada dispara depois de uma troca de janela ativa (D-014).</summary>
    private const int SettleTicks = 24; // 24 × ~16ms ≈ 380ms de cobertura

    private readonly BarOptions _options;
    private readonly SolidColorBrush _chipBrush = new(Colors.Transparent);
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _settleTimer;

    private IntPtr _handle;
    private HwndSource? _source;
    private ForegroundWatcher? _foreground;
    private uint _taskbarCreatedMessage;
    private Palette _palette = Palette.FromSystemTheme();
    private ShellState _state = ShellState.Starting;
    private RECT? _appliedRect;
    private bool _isBarVisible;
    private bool _isBlinking;
    private int _tick;
    private int _settleRemaining;
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
        LookaheadText.MouseLeftButtonUp += (_, e) => { e.Handled = true; OnTimeClicked(); };
        BreakArea.MouseLeftButtonUp += (_, e) => { e.Handled = true; Raise(BreakTaken); };
        TasksArea.MouseLeftButtonUp += (_, e) => { e.Handled = true; Raise(TasksRequested); };
        MailArea.MouseLeftButtonUp += (_, e) => { e.Handled = true; Raise(MailRequested); };
        MouseRightButtonUp += (_, e) => { e.Handled = true; ShowContextMenu(); };

        // Prioridade Normal, e não Background: em Background o tick é adiável indefinidamente
        // quando a thread de UI está ocupada, e é exatamente aí que a ordem Z precisa dele.
        // Uma chamada de SetWindowPos por segundo não é trabalho que mereça ficar na fila.
        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += OnTick;

        // Parado em repouso; só roda nos ~380ms que seguem uma troca de janela ativa.
        //
        // 16ms e não 50ms porque o intervalo é o teto da latência: uma erguida da taskbar logo
        // após um tick fica atrás até o tick seguinte. Medido com rajada de 50ms, as durações
        // saíam em múltiplos exatos de 15,6ms (a granularidade padrão do timer do Windows), pior
        // caso 79ms. A 16ms o piso do timer vira o teto da falha — um frame.
        _settleTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _settleTimer.Tick += OnSettleTick;

        SystemEvents.DisplaySettingsChanged += OnSystemChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Retângulo atual em pixels físicos, ou <c>null</c> se a barra está escondida.
    /// Os painéis usam isto como âncora.</summary>
    public RECT? CurrentRect => _appliedRect;

    public Palette CurrentPalette => _palette;

    public event EventHandler? Acknowledged;
    public event EventHandler? BreakDismissToggled;

    /// <summary>Clique no slot da pausa: "tirei essa". O app não tem como saber sozinho (D-006).</summary>
    public event EventHandler? BreakTaken;
    public event EventHandler? ReauthRequested;
    public event EventHandler? TasksRequested;
    public event EventHandler? AgendaRequested;
    public event EventHandler? MailRequested;
    public event EventHandler? SyncRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    /// <summary>Entrar na call nomeada no slot de tempo. Carrega a URL do Meet (D-016).</summary>
    public event EventHandler<string>? MeetingActivated;

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

        // Instalado aqui, na thread de UI, porque é nela que o callback será entregue (D-014).
        _foreground = new ForegroundWatcher();
        _foreground.Changed += OnForegroundChanged;

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
        RenderLookahead(state);
        RenderBreak(state);
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

        // Só o rótulo. O detalhe do cronograma vive no slot vizinho (D-023): misturar os dois numa
        // frase só fazia o estado — a resposta que o produto existe para dar — competir por largura
        // com o nome de uma reunião.
        TimeText.Text = time.Label;
        TimeText.FontWeight = time.IsBold ? FontWeights.SemiBold : FontWeights.Normal;

        // Nos humores que escalam, o bloco acende na cor do humor e o texto inverte para contrastar.
        // Nos calmos ele fica num cinza neutro, com o texto tingido: o bloco existe sempre, mas a
        // escalada continua sendo visível pela forma e não só pela cor (D-012).
        var escalates = time.IsFilled;

        TimeArea.Background = new SolidColorBrush(escalates ? background : _palette.ChipBackground);
        TimeText.Foreground = new SolidColorBrush(foreground);

        var action = time.CallUrl is { Length: > 0 }
            ? "Clique para entrar na call"
            : "Clique para abrir a agenda";

        TimeArea.ToolTip = state.IsOffline
            ? "Sem sincronização — não sei o que vem a seguir"
            : $"{time.Detail ?? time.Text}{Environment.NewLine}{action}";
    }

    /// <summary>
    /// O que vem depois de hoje. Ocupa o espaço que sobrava com o dia encerrado.
    /// <para>
    /// Cede lugar em três situações, e a ordem importa: sem sincronização não sabemos de nada;
    /// com alarme ativo o alerta tem prioridade absoluta sobre informação de conforto; e durante
    /// o expediente o slot de tempo já responde o que vem a seguir, então repetir seria ruído.
    /// </para>
    /// </summary>
    /// <summary>
    /// O slot do meio: o detalhe do compromisso de hoje ou, quando não há nenhum, o de amanhã.
    /// Um só, e o mais próximo ganha — dois lado a lado faziam a barra parecer ter duas agendas.
    /// </summary>
    private void RenderLookahead(ShellState state)
    {
        var detail = state.Time.Summary;

        // Sem compromisso hoje o slot não fica vazio: mostra o que vem depois. Cede a qualquer
        // alarme, porque informação de conforto não disputa espaço com alerta.
        if (detail is null
            && !state.IsOffline
            && state.Severity == Severity.Calm
            && state.Time.Mood is TimeMood.OffHours or TimeMood.Free)
        {
            detail = state.Lookahead;
        }

        if (state.IsOffline || string.IsNullOrEmpty(detail))
        {
            LookaheadText.Visibility = Visibility.Collapsed;
            return;
        }

        LookaheadText.Visibility = Visibility.Visible;
        LookaheadText.Text = detail;
        LookaheadText.Foreground = new SolidColorBrush(
            state.Time.NamesAnEvent ? _palette.BarForeground : _palette.Muted);
        LookaheadText.ToolTip =
            $"{state.Time.Detail ?? detail}{Environment.NewLine}Clique para abrir a agenda";
    }

    /// <summary>
    /// A pausa de descanso, no slot próprio (D-023).
    /// <para>
    /// Some por completo com a funcionalidade desligada — quem não usa não paga largura por ela.
    /// Nenhuma cor nova: reusa o verde de "Livre", porque descanso é a mesma família de estado
    /// calmo e cada cor nova custa legibilidade a todas as outras (regra 1).
    /// </para>
    /// <para>
    /// Clicar diz <i>"tirei essa"</i>. O app não infere descanso, como não infere presença em call
    /// (D-006) — e por não saber, ele se cala em vez de cobrar: pausa perdida apaga, nunca alarma.
    /// </para>
    /// </summary>
    private void RenderBreak(ShellState state)
    {
        var now = DateTimeOffset.Now;
        var pause = state.NextBreak(now);

        if (state.IsOffline || pause is null)
        {
            BreakArea.Visibility = Visibility.Collapsed;
            return;
        }

        var running = pause.IsRunningAt(now);
        var taken = state.IsBreakTaken(pause.Period);

        var (text, tip) = (running, taken) switch
        {
            (_, true) => ("tirada", $"{pause.Label} — você marcou como tirada"),
            (true, _) => ($"{Math.Max(1, (int)Math.Ceiling((pause.End - now).TotalMinutes))} min",
                          $"{pause.Label} — até {pause.End.ToLocalTime():HH:mm}"
                          + $"{Environment.NewLine}Levante e descanse · clique se já tirou"),
            _ => (pause.Start.ToLocalTime().ToString("HH:mm"),
                  $"{pause.Label} às {pause.Start.ToLocalTime():HH:mm}"
                  + $"{Environment.NewLine}Clique se já tirou"),
        };

        BreakArea.Visibility = Visibility.Visible;
        BreakArea.ToolTip = tip;
        BreakText.Text = text;

        // Acesa só durante os 15 minutos. Fora deles é informação passiva, no mesmo cinza do resto.
        var color = taken ? _palette.Muted : running ? _palette.FreeForeground : _palette.Muted;

        BreakText.Foreground = new SolidColorBrush(color);
        BreakIcon.Foreground = new SolidColorBrush(color);
        BreakArea.Background = new SolidColorBrush(
            running && !taken ? _palette.ChipBackground : Colors.Transparent);
        BreakArea.Opacity = taken ? 0.5 : 1.0;
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
    /// Chamada por três caminhos, do mais rápido ao mais lento: o evento de troca de janela ativa
    /// (<see cref="OnForegroundChanged"/>), a rajada que o segue, e o heartbeat de 1s como último
    /// recurso. É a fragilidade estrutural que D-002 aceitou ao escolher a barra flutuante.
    /// </para>
    /// </summary>
    private void AssertTopMost()
    {
        if (!_isBarVisible || _handle == IntPtr.Zero) return;

        // Só reafirma se estiver mesmo atrás. Reafirmar sem perguntar joga a barra para o topo da
        // faixa topmost a cada rodada — inclusive por cima do menu de contexto e das dicas dela
        // própria, que desapareciam atrás da barra enquanto o usuário os lia.
        if (NativeMethods.IsInFrontOf(_handle, NativeMethods.FindWindow("Shell_TrayWnd", null)))
            return;

        SetWindowPos(_handle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Outra janela virou a ativa — o instante em que o explorer ergue o <c>Shell_TrayWnd</c>.
    /// <para>
    /// Reafirmar uma vez não basta: o explorer ergue a taskbar <b>depois</b> que a ativação se
    /// completa, então a reafirmação imediata chega cedo demais e perde a corrida. Daí a rajada
    /// curta que cobre os ~380ms seguintes (D-014).
    /// </para>
    /// </summary>
    private void OnForegroundChanged(object? sender, EventArgs e)
    {
        AssertTopMost();

        _settleRemaining = SettleTicks;
        if (!_settleTimer.IsEnabled) _settleTimer.Start();
    }

    private void OnSettleTick(object? sender, EventArgs e)
    {
        AssertTopMost();

        // Autodesarme: em repouso nenhum timer de 16ms pode continuar rodando (critério de
        // aceite 10 — consumo indistinguível de zero).
        if (--_settleRemaining <= 0) _settleTimer.Stop();
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
        // Rede de segurança do caminho rápido: cobre as erguidas da taskbar que acontecem sem
        // troca de janela ativa, e o caso de o sistema ter recusado o hook. Uma chamada de
        // SetWindowPos por segundo.
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
    /// Clicar na situação temporal **entra na call**, quando a reunião nomeada ali tem link de
    /// Meet; sem link, abre a agenda (D-016). Nunca reconhece alerta — reconhecer é exclusivo da
    /// área de motivo, para que o gesto tenha um lugar só e previsível.
    /// </summary>
    private void OnTimeClicked()
    {
        if (_state.IsOffline)
        {
            Raise(ReauthRequested);
            return;
        }

        if (_state.Time.CallUrl is { Length: > 0 } url)
        {
            MeetingActivated?.Invoke(this, url);
            return;
        }

        Raise(AgendaRequested);
    }

    private void ShowContextMenu()
    {
        var menu = new ContextMenu { PlacementTarget = this };

        // O clique esquerdo no slot passou a entrar na call (D-016), então a agenda precisa de um
        // caminho que não dependa dele — senão a superfície perde o acesso a S3 durante reunião.
        if (_state.Time.CallUrl is { Length: > 0 } url)
        {
            var join = new MenuItem { Header = "Entrar na call", FontWeight = FontWeights.SemiBold };
            join.Click += (_, _) => MeetingActivated?.Invoke(this, url);
            menu.Items.Add(join);
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(MenuItemFor("Sincronizar agora", SyncRequested));
        menu.Items.Add(MenuItemFor("Abrir agenda", AgendaRequested));
        menu.Items.Add(MenuItemFor("Abrir tarefas", TasksRequested));

        if (_state.CanAcknowledge)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor("Reconhecer alerta", Acknowledged));
        }

        // Só o gesto do dia aparece aqui. Ligar e desligar a funcionalidade é decisão de
        // instalação e mora no appsettings — colocá-la no menu convidaria a desligar de vez num
        // dia ruim, que é justamente o dia em que a pausa importa mais.
        if (_state.Breaks.Count > 0 || _state.BreaksDismissed)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor(
                _state.BreaksDismissed ? "Restaurar pausas de hoje" : "Hoje não quero pausa",
                BreakDismissToggled));
        }

        // Sempre disponível, e não só no estado Offline: mudar de escopo exige um novo consent
        // mesmo com o login válido, e sem esta entrada não haveria como pedir isso.
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Reconectar ao Google", ReauthRequested));

        menu.Items.Add(MenuItemFor("Configurações…", SettingsRequested));

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
        _settleTimer.Stop();
        _settleTimer.Tick -= OnSettleTick;
        StopBlink();

        if (_foreground is not null)
        {
            _foreground.Changed -= OnForegroundChanged;
            _foreground.Dispose();
            _foreground = null;
        }

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
