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
    /// <summary>Período do piscar. I8 exige ≥ 1s; ida e volta dão 1,2s.</summary>
    private const int BlinkHalfPeriodMs = 600;

    public static TimeSpan BlinkPeriod => TimeSpan.FromMilliseconds(BlinkHalfPeriodMs * 2);

    private readonly SolidColorBrush _chipBrush = new(Colors.Transparent);

    /// <summary>Irmão do <see cref="_chipBrush"/> para o modo contorno da invariante I9.</summary>
    private readonly SolidColorBrush _chipBorderBrush = new(Colors.Transparent);

    private readonly SolidColorBrush _statusBrush = new(Colors.Transparent);

    /// <summary>
    /// Quanto tempo o "Abrindo a call…" fica no ar. Curto de propósito: é confirmação de clique,
    /// não status — o navegador leva mais que isso e tudo bem, o que faltava era saber que o
    /// clique pegou.
    /// </summary>
    private static readonly TimeSpan OpeningFeedback = TimeSpan.FromSeconds(3);

    /// <summary>Até quando mostrar a confirmação. Instante, não estado.</summary>
    private DateTimeOffset _openingUntil;
    private bool _isStatusBlinking;
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
        ReasonChip.BorderBrush = _chipBorderBrush;
        TimeArea.Background = _statusBrush;

        // Tamanho inicial em DIP só para o WPF medir o conteúdo; a posição e o tamanho reais
        // são impostos por SetWindowPos em pixels físicos.
        Width = _options.Slots * _options.SlotWidthDip;
        Height = 40;

        ReasonChip.MouseLeftButtonUp += (_, e) => { e.Handled = true; OnReasonClicked(); };
        // Os dois slots têm sentidos diferentes (D-023): o bloco é o estado, o texto é o
        // compromisso. Clicar no estado diz "eu vi"; clicar no compromisso age sobre ele.
        TimeArea.MouseLeftButtonUp += (_, e) => { e.Handled = true; OnStatusClicked(); };
        LookaheadText.MouseLeftButtonUp += (_, e) => { e.Handled = true; OnTimeClicked(); };
        BreakArea.MouseLeftButtonUp += (_, e) => { e.Handled = true; OrReauth(BreakTaken); };
        PautaArea.MouseLeftButtonUp += (_, e) => { e.Handled = true; OnPautaClicked(); };
        TasksArea.MouseLeftButtonUp += (_, e) => { e.Handled = true; OrReauth(TasksRequested); };
        MailArea.MouseLeftButtonUp += (_, e) => { e.Handled = true; OrReauth(MailRequested); };
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

    /// <summary>
    /// "Já saí desta reunião", ou o desfazer disso (D-041). Carrega a ocorrência, e não o evento,
    /// porque quem alterna precisa dizer <b>qual</b> — e no instante do desfazer a reunião já não
    /// está na agenda que o humor enxerga.
    /// </summary>
    public event EventHandler<string>? MeetingLeftToggled;

    /// <summary>"Agora não": empurra a pausa 30 min para frente. Repetível.</summary>
    public event EventHandler? BreakPostponed;

    /// <summary>"Estou tirando agora": fixa a pausa deste período na hora atual.</summary>
    public event EventHandler? BreakStartedNow;

    /// <summary>Escolha do evento ativo entre reunioes sobrepostas (SEVERITY 8). Carrega o id.</summary>
    public event EventHandler<string>? ActiveEventChosen;
    public event EventHandler? ReauthRequested;

    /// <summary>Barra cinza por sync caído: o gesto é tentar de novo, não reautenticar.</summary>
    public event EventHandler? SyncRetryRequested;

    /// <summary>Abrir a pauta de uma reunião (D-052). Carrega o id do evento.</summary>
    public event EventHandler<string>? PautaRequested;
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

        // EffectiveSeverity, e não Severity: Offline precede e ANULA a escala (§0). Ler o campo
        // cru deixaria uma severidade velha pintar o chip enquanto os dados já não são confiáveis.
        var (chipBackground, chipForeground) = _palette.For(state);
        var showChip = state.IsOffline || state.EffectiveSeverity > Severity.Calm;

        // I9: se o chip cairia na mesma família de cor do slot de tempo, ele abre mão do
        // preenchimento e vira contorno. Sem isto os dois viram pílulas iguais lado a lado e o
        // olho não separa qual está falando de quê — foi assim que o usuário encontrou o problema.
        //
        // Quem cede é o chip, não o slot: o slot está lá o dia inteiro e mudar a forma dele seria
        // mais perturbador, e tirar o preenchimento dele apagaria a escalada do Overrun (D-025).
        var outlined = showChip
            && !state.IsOffline
            && ColorVocabulary.Collide(state.Time.Mood, state.EffectiveSeverity);

        var ink = _palette.OutlineFor(state.EffectiveSeverity);

        StopBlink();
        _chipBrush.Color = showChip && !outlined ? chipBackground : Colors.Transparent;
        _chipBorderBrush.Color = outlined ? ink : Colors.Transparent;
        ReasonChip.BorderThickness = new Thickness(outlined ? 1.2 : 0);

        ReasonText.Text = state.Reason;
        ReasonText.Foreground = new SolidColorBrush(
            (showChip, outlined) switch
            {
                (_, true) => ink,
                (true, _) => chipForeground,
                _ => _palette.BarForeground,
            });

        // Some quando não há motivo, e também quando o motivo é o mesmo que o slot já conta (D-033).
        //
        // Collapsed e não Hidden: o D-033 usou Hidden para o chip guardar o lugar na grade e o
        // texto ao lado não saltar de largura. **Foi o negócio errado** — Hidden reserva a largura
        // do último motivo exibido, e o usuário encontrou o resultado: um vão morto do tamanho de
        // "Metade do dia: 2 tarefas abertas" no meio da barra, enquanto o título da reunião
        // truncava por falta de espaço. Estabilidade de layout não vale o espaço da informação
        // que importa (D-037).
        ReasonChip.Visibility = string.IsNullOrEmpty(state.Reason) || state.ChipRepeatsTime
            ? Visibility.Collapsed
            : Visibility.Visible;

        // §1.1: nível 3 não reconhecido por 5 min passa a piscar âmbar↔vermelho.
        // Se o usuário desligou animações no Windows, fica vermelho sólido (invariante I8).
        if (state is { IsEscalated: true, IsOffline: false }
            && state.EffectiveSeverity == Severity.Critical
            && SystemParameters.ClientAreaAnimation)
        {
            // Em contorno, animar o fundo não mostraria nada: quem carrega a cor é a borda. A I8
            // continua valendo — muda onde o piscar acontece, não se ele acontece.
            StartBlink(_palette.CriticalBackground, _palette.AttentionBackground, outlined);
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

        RenderPauta(state, counterBrush, iconBrush);

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

        StopStatusBlink();
        _statusBrush.Color = escalates ? background : _palette.ChipBackground;
        TimeText.Foreground = new SolidColorBrush(foreground);

        // §1.1 e regra 2: passados 5 min sem reconhecimento, o alarme deixa de ser sólido e passa
        // a piscar âmbar↔vermelho. Ele escala em vez de decair, e só o clique o encerra.
        //
        // Com animações desligadas no Windows fica sólido (invariante I8): quem desligou animação
        // costuma ter motivo — enjoo, epilepsia fotossensível, preferência — e o vermelho sólido
        // já comunica o essencial.
        if (time.IsEscalated && !state.IsOffline && SystemParameters.ClientAreaAnimation)
            StartStatusBlink(_palette.CriticalBackground, _palette.AttentionBackground);

        // A dica tem de dizer o que este clique faz, e não o que algum clique da barra faz: entrar
        // na call é do texto ao lado, marcado com ▶ (D-037, D-038).
        var action = state.CanAcknowledge
            ? "Clique para reconhecer — eu vi"
            : "Clique para ver a agenda do dia";

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
    /// O contador de pauta (D-052): quantos assuntos você ainda não disse nesta reunião.
    /// <para>
    /// <b>Só existe durante uma reunião que tem pauta.</b> Reunião sem assunto nenhum não ganha
    /// indicador — a esmagadora maioria é assim, e um contador permanente para elas seria ruído
    /// fixo ao lado do que importa.
    /// </para>
    /// <para>
    /// Âmbar enquanto houver assunto pendente, neutro quando chega a zero. Terminar a pauta apaga
    /// a cor, e essa é a recompensa: a barra volta ao normal porque você falou tudo. Nunca pisca,
    /// nunca escala, nunca entra na arbitragem do §4 — não é sinal, é contador.
    /// </para>
    /// <para>
    /// A invariante <b>I6</b> continua valendo: ela proíbe o contador <b>herdar</b> a cor do
    /// alarme, e este nunca muda com a severidade da barra. O âmbar é próprio, pelo mesmo motivo
    /// do ponto de <c>Rsvp.NeedsAction</c> — a mesma cor querendo dizer a mesma coisa.
    /// </para>
    /// </summary>
    private void RenderPauta(ShellState state, Brush counterBrush, Brush iconBrush)
    {
        // Offline não desenha número nenhum (I7): o retorno cedo do BuildState deixa PautaTotal em
        // zero, então esta linha já cobre o caso sem precisar consultar IsOffline.
        if (state.PautaTotal == 0)
        {
            PautaArea.Visibility = Visibility.Collapsed;
            return;
        }

        PautaArea.Visibility = Visibility.Visible;
        PautaCount.Text = state.PautaPending.ToString();

        var pendente = state.PautaPending > 0;

        PautaCount.Foreground = pendente
            ? new SolidColorBrush(_palette.AttentionBackground)
            : counterBrush;

        PautaIcon.Foreground = pendente
            ? new SolidColorBrush(_palette.AttentionBackground)
            : iconBrush;

        PautaArea.ToolTip = pendente
            ? $"{Plural(state.PautaPending, "assunto", "assuntos")} para falar — clique para abrir"
            : "Pauta concluída — clique para rever";
    }

    private static string Plural(int n, string um, string varios) =>
        n == 1 ? $"1 {um}" : $"{n} {varios}";

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

        var joinable = state.Time.CallUrl is { Length: > 0 };

        LookaheadText.Visibility = Visibility.Visible;

        // Retorno do clique: abrir o navegador demora, e sem isto o gesto não devolve nada — você
        // fica sem saber se registrou. Mesmo motivo do "Abrindo o navegador…" do re-consent.
        // Transitório de verdade: o próximo render depois da janela já traz o texto real de volta,
        // porque um aviso preso viraria dado velho com cara de atual (regra 10).
        LookaheadText.Text = DateTimeOffset.Now < _openingUntil
            ? "Abrindo a call…"

            // O ▶ diz que dali se entra, antes de precisar passar o mouse. Sem cor: afordância não
            // precisa gastar o orçamento da regra 1, e o espaço agora sobra porque o chip repetido
            // deixou de ser desenhado (D-033).
            : joinable ? $"▶ {detail}" : detail;

        // A cor da urgência mora em **um** lugar só: na pílula quando ela é preenchida, e aqui
        // quando ela é apenas tingida. Em `Approaching` a pílula não preenche, então é a contagem
        // que precisa acender — em branco ela não inspirava urgência nenhuma, e a próxima call é a
        // coisa mais importante do dia (D-037).
        //
        // Não é uma terceira área colorida (I1): é a mesma área de tempo, que o §0.5 já define
        // como um vocabulário só, se estendendo pelo detalhe que a acompanha.
        LookaheadText.Foreground = new SolidColorBrush(
            !state.Time.NamesAnEvent ? _palette.Muted
            : state.Time.Mood == TimeMood.Approaching ? _palette.AttentionInk
            : _palette.BarForeground);

        // Dizia "Clique para abrir a agenda" mesmo quando o clique entrava na call — a dica
        // contradizia o que o gesto fazia.
        LookaheadText.ToolTip = $"{state.Time.Detail ?? detail}{Environment.NewLine}"
            + (joinable ? "Clique para entrar na call" : "Clique para abrir a agenda");
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
            (_, true) => ("tirada",
                          $"{pause.Label} — você marcou como tirada"
                          + $"{Environment.NewLine}Clique de novo se foi sem querer"),
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

    private void StartStatusBlink(Color from, Color to)
    {
        _statusBrush.BeginAnimation(SolidColorBrush.ColorProperty, Blink(from, to));
        _isStatusBlinking = true;
    }

    private void StopStatusBlink()
    {
        if (!_isStatusBlinking) return;

        _statusBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _isStatusBlinking = false;
    }

    private static ColorAnimation Blink(Color from, Color to) => new()
    {
        From = from,
        To = to,
        Duration = TimeSpan.FromMilliseconds(BlinkHalfPeriodMs),
        AutoReverse = true,
        RepeatBehavior = RepeatBehavior.Forever,
        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
    };

    /// <param name="onBorder">
    /// Chip em modo contorno (I9): quem carrega a cor é a borda, então é ela que pisca. Animar o
    /// fundo transparente não mostraria nada.
    /// </param>
    private void StartBlink(Color from, Color to, bool onBorder = false)
    {
        var animation = new ColorAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(BlinkHalfPeriodMs), // ida+volta = 1,2s de período
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };

        (onBorder ? _chipBorderBrush : _chipBrush)
            .BeginAnimation(SolidColorBrush.ColorProperty, animation);

        _isBlinking = true;
    }

    private void StopBlink()
    {
        if (!_isBlinking) return;

        // Para os dois sem perguntar qual estava animando: o modo pode ter mudado entre um render
        // e outro, e uma animação esquecida continuaria pintando por cima do valor novo.
        _chipBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _chipBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
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

        // Só reafirma se estiver mesmo coberta. Reafirmar sem perguntar joga a barra para o topo
        // da faixa topmost a cada rodada — inclusive por cima do menu de contexto e das dicas dela
        // própria, que desapareciam atrás da barra enquanto o usuário os lia (D-021).
        if (OwnsItsPixels()) return;

        // Tentativa barata: sair e voltar da faixa topmost. Reafirmar HWND_TOPMOST numa janela que
        // **já é** topmost o Windows ignora, então a saída e o retorno forçam a reordenação.
        SetWindowPos(_handle, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        SetWindowPos(_handle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        if (OwnsItsPixels()) return;

        // Segunda tentativa: colocação **relativa**, logo acima do próprio Shell_TrayWnd, em vez
        // de pedir o topo genérico da faixa. Quando o shell ergue a taskbar, pedir HWND_TOPMOST
        // continua devolvendo uma posição abaixo dela; apontar para ela é explícito.
        if (NativeMethods.FindWindow("Shell_TrayWnd", null) is { } tray && tray != IntPtr.Zero)
        {
            SetWindowPos(_handle, tray, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            if (OwnsItsPixels()) return;
        }

        // Não bastou: depois de o menu Iniciar erguer a taskbar, reordenar não devolve a barra
        // para cima dela. Re-exibir devolve — uma janela recém-mostrada entra no **topo** da faixa
        // topmost, e não na posição que tinha antes.
        //
        // Só acontece com a barra já enterrada, ou seja, invisível: o piscar que isto causaria não
        // tem como ser visto, e é o preço de um caminho que sem ele não se recupera sozinho.
        if (_appliedRect is not { } rect) return;

        ShowWindow(_handle, SW_HIDE);

        SetWindowPos(_handle, HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// A barra é mesmo quem recebe o pixel — e portanto o clique — no próprio retângulo?
    /// <para>
    /// <b>A pergunta direta, no lugar de um proxy.</b> O D-021 perguntava "estou na frente do
    /// <c>Shell_TrayWnd</c>?", que respondia certo para o caso que ele resolvia e errado para o
    /// menu Iniciar: com ele aberto a barra sumia enquanto <i>todos</i> os indicadores diziam que
    /// estava tudo bem — visível, topmost, no retângulo certo, não encoberta na enumeração de
    /// janelas e sem <i>cloaking</i> do DWM. Só o <c>WindowFromPoint</c> discordava, e é ele que
    /// decide de quem é o pixel (D-035).
    /// </para>
    /// </summary>
    private bool OwnsItsPixels()
    {
        if (_appliedRect is not { } rect) return false;

        var owner = NativeMethods.WindowAt(
            rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));

        if (owner == _handle) return true;

        // Janela nossa por cima é legítima: é o menu de contexto ou a dica da própria barra, e
        // reafirmar aqui os enterraria — que era o defeito que o D-021 consertou.
        return owner != IntPtr.Zero && BelongsToThisProcess(owner);
    }

    private static bool BelongsToThisProcess(IntPtr window)
    {
        NativeMethods.GetWindowThreadProcessId(window, out var owner);

        return owner == (uint)Environment.ProcessId;
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
            RaiseOfflineGesture();
            return;
        }

        // Sobreposição sem escolha (§8): o chip está fazendo uma pergunta, e o clique responde.
        // Reconhecer aqui silenciaria a pergunta sem resolvê-la — os sinais continuariam sem saber
        // qual reunião é a sua.
        if (_state.ActiveEventChoices.Count > 1)
        {
            ShowActiveEventSelector();
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
    /// <summary>
    /// Clique no bloco de estado. Havendo alarme, ele é reconhecido — <b>é a promessa da regra 2</b>:
    /// nenhum nível 3 é inescapável, e a saída custa um clique.
    /// <para>
    /// Reconhecer ganha de entrar na call, porque com <c>Estourou</c> ou <c>Encerrando</c> você já
    /// está (ou esteve) na reunião, e entrar de novo não é o que se quer. Entrar continua a um
    /// clique de distância no texto do compromisso, ao lado, e no menu de contexto (D-016).
    /// </para>
    /// </summary>
    private void OnStatusClicked()
    {
        if (_state.IsOffline)
        {
            RaiseOfflineGesture();
            return;
        }

        if (_state.CanAcknowledge)
        {
            Raise(Acknowledged);
            return;
        }

        // Abre a agenda do dia — **não** entra na call. Antes isto caía em OnTimeClicked, e o
        // resultado é que durante uma reunião os dois cliques esquerdos da barra faziam a mesma
        // coisa: entrar na call que já estava aberta. A lista do dia ficava sem porta, sobrando só
        // o menu do botão direito, e o usuário não achou (D-038).
        //
        // Agora cada área tem um significado só, e o ▶ do D-037 é o que distingue: quem tem o
        // glifo entra na call, quem não tem mostra o dia.
        Raise(AgendaRequested);
    }

    private void OnTimeClicked()
    {
        if (_state.IsOffline)
        {
            RaiseOfflineGesture();
            return;
        }

        if (_state.Time.CallUrl is { Length: > 0 } url)
        {
            ShowOpening();
            MeetingActivated?.Invoke(this, url);
            return;
        }

        Raise(AgendaRequested);
    }

    /// <summary>
    /// Confirma o clique enquanto o navegador não aparece. Sem isto o gesto não devolve nada e não
    /// dá para saber se registrou — o mesmo problema que o re-consent resolve com "Abrindo o
    /// navegador…".
    /// </summary>
    private void ShowOpening()
    {
        _openingUntil = DateTimeOffset.Now + OpeningFeedback;

        RenderLookahead(_state); // na hora, sem esperar o ciclo de render

        // Um disparo só para devolver o texto no fim da janela. Sem ele a mensagem ficaria no ar
        // até o próximo render de rotina, que pode demorar mais que ela deveria durar.
        var restore = new DispatcherTimer(DispatcherPriority.Background) { Interval = OpeningFeedback };

        restore.Tick += (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            RenderLookahead(_state);
        };

        restore.Start();
    }

    /// <summary>
    /// O seletor de evento ativo (§8). Menu curto com as reuniões concorrentes; a escolhida passa
    /// a ser a única que alimenta os sinais do §2.1.
    /// <para>
    /// Um menu, e não uma janela: a pergunta é de dois segundos e abrir diálogo para ela custaria
    /// mais atenção do que a dúvida vale. O item em negrito é o que o padrão determinístico já
    /// assumiu, para o clique confirmar o provável em vez de escolher no escuro.
    /// </para>
    /// </summary>
    private void ShowActiveEventSelector()
    {
        var menu = new ContextMenu { PlacementTarget = this };

        menu.Items.Add(new MenuItem
        {
            Header = "Em qual você está?",
            IsEnabled = false,
        });
        menu.Items.Add(new Separator());

        var padrao = _state.ActiveEventChoices[0].Id;

        foreach (var choice in _state.ActiveEventChoices)
        {
            var item = new MenuItem
            {
                Header = $"{choice.Start.ToLocalTime():HH:mm}–{choice.End.ToLocalTime():HH:mm}  {choice.Title}",
                FontWeight = choice.Id == padrao ? FontWeights.SemiBold : FontWeights.Normal,
            };

            var id = choice.Id;
            item.Click += (_, _) => ActiveEventChosen?.Invoke(this, id);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
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

        // Offline, os painéis somem do menu (§0). Eles só teriam o retrato do último sync
        // bem-sucedido para mostrar, e apresentá-lo sem ressalva é o que a regra 10 proíbe. O
        // contador já virou "—" para não mentir; abrir o painel desmentiria o contador.
        if (!_state.IsOffline)
        {
            menu.Items.Add(MenuItemFor("Abrir agenda", AgendaRequested));
            menu.Items.Add(MenuItemFor("Abrir tarefas", TasksRequested));
        }

        if (_state.CanAcknowledge)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor("Reconhecer alerta", Acknowledged));
        }

        // "Já saí" é irmão do "eu vi", e não sinônimo: um apaga o alarme, o outro diz que a
        // reunião acabou antes da hora. Sem ele a barra seguia contando o tempo de uma call
        // encerrada e chegava a acender o vermelho de "estourou" por ela (D-041).
        // Alterna, como o gesto da pausa: um clique sem querer se desfaz com outro. Um gesto que
        // grava em disco e vale o resto do dia precisa de volta.
        if (_state.LeavableOccurrence is { } corrente)
        {
            menu.Items.Add(new Separator());

            var sair = new MenuItem { Header = "Encerrei esta reunião" };
            sair.Click += (_, _) => MeetingLeftToggled?.Invoke(this, corrente);
            menu.Items.Add(sair);
        }
        else if (_state.ReopenableOccurrence is { } encerrada)
        {
            menu.Items.Add(new Separator());

            var voltar = new MenuItem { Header = $"Reabrir “{_state.ReopenableTitle}”" };
            voltar.Click += (_, _) => MeetingLeftToggled?.Invoke(this, encerrada);
            menu.Items.Add(voltar);
        }

        // Só o gesto do dia aparece aqui. Ligar e desligar a funcionalidade é decisão de
        // instalação e mora no appsettings — colocá-la no menu convidaria a desligar de vez num
        // dia ruim, que é justamente o dia em que a pausa importa mais.
        // Offline não avalia sinal nenhum (§0), e pausa é sinal: sem saber a agenda, o Tempus não
        // tem o que sugerir nem o que adiar.
        if (_state.BreaksEnabled && !_state.IsOffline)
        {
            menu.Items.Add(new Separator());

            // "Agora não" é diferente de "hoje não": adiar empurra 30 min e é repetível; dispensar
            // encerra o assunto até amanhã. Separar os dois evita que um dia corrido desligue a
            // folga por inteiro quando bastaria empurrá-la.
            // Sempre disponível: a folga é sugestão, não agendamento. Ela pode acontecer numa
            // hora que o Tempus não previu, e depois que as duas do dia venceram este é o único
            // gesto que sobra — sem ele, uma folga perdida fica inalcançável.
            if (!_state.BreaksDismissed)
                menu.Items.Add(MenuItemFor("Tirar pausa agora", BreakStartedNow));

            // Só quando há pausa por vir. Depois que as duas passaram não há o que empurrar, e
            // oferecer a opção seria prometer uma ação que não faz nada.
            if (!_state.BreaksDismissed && _state.NextBreak(DateTimeOffset.Now) is not null)
                menu.Items.Add(MenuItemFor("Adiar pausa em 30 min", BreakPostponed));

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

    /// <summary>
    /// Offline, <b>a barra inteira</b> leva ao re-consent (<c>SEVERITY.md</c> §0 e §6) — não só o
    /// slot de tempo.
    /// <para>
    /// Sem isto, clicar no contador de tarefas abria o painel com o retrato velho do último sync
    /// bem-sucedido: exatamente "mostrar dado velho como se fosse atual", que a regra 10 proíbe. O
    /// contador já diz <c>—</c> justamente para não mentir; o painel atrás dele não pode desmentir.
    /// </para>
    /// </summary>
    private void OrReauth(EventHandler? handler)
    {
        if (_state.IsOffline) RaiseOfflineGesture();
        else Raise(handler);
    }

    /// <summary>
    /// O gesto da barra cinza, escolhido pela <b>causa</b> do offline. Só aqui: a entrada
    /// "Reconectar ao Google" do menu continua sempre pedindo consent, porque trocar de escopo
    /// exige isso mesmo com o login válido.
    /// </summary>
    private void RaiseOfflineGesture() =>
        Raise(_state.OfflineNeedsConsent ? ReauthRequested : SyncRetryRequested);

    /// <summary>
    /// Clique no contador de pauta. Sem <c>OrReauth</c> de propósito: o contador nem existe em
    /// <c>Offline</c>, então não há o caso que aquele desvio protege.
    /// </summary>
    private void OnPautaClicked()
    {
        if (_state.PautaEventId is not { } id) return;

        PautaRequested?.Invoke(this, id);
    }

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
        StopStatusBlink();

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
