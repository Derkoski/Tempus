using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Tempus.Domain;
using Tempus.Fake;
using Tempus.Interop;
using Tempus.Shell;
using Tempus.Sync;

namespace Tempus;

public partial class App : Application
{
    /// <summary>
    /// Com que frequência o estado é reconstruído a partir do último retrato. Não busca nada na
    /// rede: é só recontar o tempo. Cinco segundos porque a barra agora exibe uma contagem
    /// regressiva — a 20s ela ficaria visivelmente atrasada perto da virada de minuto.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private Mutex? _singleInstance;
    private IShellSurface? _surface;
    private DispatcherTimer? _refresh;

    private GoogleSync? _sync;
    private SyncSnapshot _snapshot = SyncSnapshot.Starting;
    private TimeThresholds _thresholds = TimeThresholds.Default;
    private WorkDayOptions _workDay = WorkDayOptions.Default;
    private BreakOptions _breaks = BreakOptions.Default;
    private BreakDismissalStore? _dismissals;
    private readonly SeverityGate _gate = new();
    private DayEndedOptions _dayEnded = DayEndedOptions.Default;

    private ToastOptions _toastOptions = ToastOptions.Default;
    private ToastChannel? _toasts;

    private PendingWriteStore? _writeStore;
    private WriteQueue? _writes;

    /// <summary>
    /// Chaves de toast já emitidas hoje (§5). Em memória, como o <see cref="_activeEventId"/>: um
    /// restart com o vermelho ainda de pé reemitir o aviso é o comportamento <b>certo</b>, porque
    /// a situação continua sem resolução. Persistir isto compraria silêncio depois de um restart —
    /// que é exatamente quando o usuário mais precisa de um lembrete do que ficou aberto.
    /// </summary>
    private readonly HashSet<string> _toastsSent = [];

    /// <summary>
    /// Etiqueta → ocorrência dos toasts que este processo emitiu e que podem estar na tela.
    /// <para>
    /// Existe por causa do aviso <b>fixo</b> (D-039): ele não some sozinho, então alguém precisa
    /// saber que ele está lá para retirá-lo quando o assunto acabar. Não se confunde com o
    /// <see cref="_toastsSent"/>, que responde "já saiu?" e nunca esquece; este responde "ainda
    /// está de pé?" e esvazia.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, string> _standing = [];

    /// <summary>
    /// O sinal que a barra está exibindo, guardado pelo <see cref="BuildState"/> para o
    /// <see cref="Rerender"/> decidir sobre interromper. Fica aqui, e não no
    /// <see cref="ShellState"/>, porque o estado é o que a superfície desenha — e toast não é
    /// desenho, é outra saída.
    /// </summary>
    private Signal? _shownSignal;

    /// <summary>
    /// Qual reuniao o usuario declarou estar atendendo entre sobrepostas (SEVERITY 8).
    /// <para>
    /// Em memoria de proposito: a escolha vale so pela janela de sobreposicao, que dura minutos, e
    /// perde-la num restart degrada para o padrao deterministico — que ja e uma resposta razoavel —
    /// em vez de degradar para nada. Persistir custaria um terceiro arquivo de estado por um ganho
    /// que dura o intervalo entre duas reunioes.
    /// </para>
    /// </summary>
    private string? _activeEventId;
    private AcknowledgementStore? _acks;
    private HashSet<string> _acknowledged = [];
    private DateOnly _acksDay;
    private UserSettingsStore? _userStore;
    private UserSettings _user = new();
    private string _settingsPath = string.Empty;

    private FakeStateSource? _demo;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Antes de qualquer janela: depois de a primeira subir, o shell já agrupou o processo sob
        // a identidade herdada de quem o lançou, e o toast sairia com o nome errado — ou não sairia.
        AppIdentity.Declare();

        // Antes do mutex de instância única, de propósito: a sonda é diagnóstico e precisa rodar
        // com a barra já no ar, que é a situação em que se desconfia das notificações.
        if (e.Args.Contains("--toast-probe", StringComparer.OrdinalIgnoreCase))
        {
            RunToastProbe();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--dump-tasks", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunTaskDumpAsync();
            return;
        }

        if (e.Args.Contains("--dump-agenda", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunAgendaDumpAsync();
            return;
        }

        if (e.Args.Contains("--pauta-probe", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunPautaProbeAsync();
            return;
        }

        // Duas barras sobrepostas no mesmo pixel são um bug difícil de diagnosticar.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Tempus.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var settingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        _thresholds = ThresholdsLoader.Load(settingsPath);
        _dayEnded = ThresholdsLoader.LoadDayEnded(settingsPath);
        _toastOptions = ThresholdsLoader.LoadToasts(settingsPath);
        _dismissals = new BreakDismissalStore(GoogleOptions.DataDirectory);
        _acks = new AcknowledgementStore(GoogleOptions.DataDirectory);
        _acksDay = DateOnly.FromDateTime(DateTime.Today);
        _acknowledged = _acks.Load(_acksDay);
        _userStore = new UserSettingsStore(GoogleOptions.DataDirectory);
        _writeStore = new PendingWriteStore(GoogleOptions.DataDirectory);
        _settingsPath = settingsPath;

        // Duas camadas: appsettings é padrão de fábrica, %APPDATA% é o que o usuário escolheu.
        var google = GoogleOptions.Load(settingsPath);
        _user = _userStore.Load();
        ApplyUserSettings();

        var isDemo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);

        // Primeira execução: sem e-mail configurado a tela é obrigatória (D-020). O modo demo
        // escapa — ele existe para exercitar a UI sem conta, e exigir conta o inutilizaria.
        if (!isDemo && !_user.HasLoginHint && !ShowSettings(isFirstRun: true))
        {
            Shutdown();
            return;
        }

        var surface = new FloatingBarSurface(BarOptions.Load(settingsPath));
        _surface = surface;

        surface.MailRequested += (_, _) => Open("https://mail.google.com");
        surface.MeetingActivated += (_, url) => Open(url);
        surface.ExitRequested += (_, _) => Shutdown();

        // Gesto do dia, não configuração: ligar/desligar a funcionalidade é do appsettings, e só
        // dispensar a folga de hoje passa pelo menu.
        surface.BreakDismissToggled += (_, _) => UpdateBreakState(
            state => state with { Dismissed = !state.Dismissed, Taken = [], Postponed = [] });

        // "Estou tirando agora." Vale mesmo com as pausas do dia já vencidas — é o caso em que
        // este gesto existe, e sem ele a folga perdida ficava inalcançável.
        surface.BreakStartedNow += (_, _) => UpdateBreakState(state =>
        {
            var now = DateTimeOffset.Now;
            var period = now < AtToday(now, _workDay.MiddayHour, _workDay.MiddayMinute)
                ? BreakPeriod.Morning
                : BreakPeriod.Afternoon;

            return state.WithStarted(period, now);
        });

        // "Agora não." Empurra 30 min; o planejador acha a próxima janela livre a partir dali.
        surface.BreakPostponed += (_, _) => UpdateBreakState(state =>
        {
            var now = DateTimeOffset.Now;
            if (BuildState(_snapshot).NextBreak(now) is not { } pause) return state;

            // O piso conta a partir do que vier mais tarde: a hora atual ou a própria pausa. Com
            // "agora + 30" adiar às 15:58 uma pausa marcada para 17:50 a puxaria para 16:30 —
            // antecipar, não adiar. Assim cada clique sempre empurra para frente.
            var from = pause.Start > now ? pause.Start : now;

            return state.WithPostponed(pause.Period, from.AddMinutes(30));
        });

        // "Tirei essa." O app não infere descanso, como não infere presença em call (D-006).
        // Alterna: um clique sem querer se desfaz com outro clique. Um gesto de um clique só, que
        // grava em disco e vale o dia inteiro, precisa de volta — senão o erro dura até amanhã.
        surface.BreakTaken += (_, _) => UpdateBreakState(state =>
        {
            if (BuildState(_snapshot).NextBreak(DateTimeOffset.Now) is not { } pause) return state;

            return state.Taken.Contains(pause.Period)
                ? state with { Taken = [.. state.Taken.Where(p => p != pause.Period)] }
                : state with { Taken = [.. state.Taken, pause.Period] };
        });

        // "Já saí desta reunião" (D-041). Fiado aqui, e não no StartGoogle, porque o gesto é local
        // — não escreve nada no Google — e porque o demo também mostra o item no menu.
        surface.MeetingLeftToggled += (_, occurrence) => ToggleMeetingLeft(occurrence);

        surface.SettingsRequested += (_, _) => ShowSettings(isFirstRun: false);

        // O modo demo fica de fora: ele percorre os estados a cada 4 s, e um demo que dispara
        // notificação de verdade seria insuportável — e enganoso, porque anunciaria dado falso.
        if (_toastOptions.Enabled && !isDemo)
        {
            _toasts = new ToastChannel();
            _toasts.Activated += (_, argument) => OnToastActivated(argument);
            _toasts.Open();
        }

        if (isDemo)
            StartDemo(surface, e.Args.Contains("--fail-writes", StringComparer.OrdinalIgnoreCase));
        else
            StartGoogle(surface, google with { LoginHint = _user.LoginHint }, SyncOptions.Load(settingsPath));

        surface.Show();

        _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _refresh.Tick += (_, _) => Rerender();
        _refresh.Start();
    }

    // ---------------------------------------------------------------- Google real

    private void StartGoogle(FloatingBarSurface surface, GoogleOptions options, SyncOptions cadence)
    {
        var auth = new GoogleAuth(
            GoogleOptions.ClientSecretPath, GoogleOptions.TokenDirectory, options.LoginHint);

        var sync = new GoogleSync(
            auth, cadence, options, cadence.CompletedDays, SyncLog.InDataDirectory());

        _sync = sync;

        // A fila é criada aqui porque precisa do executor, que é este sync. O store, não: ele já
        // existe desde a subida, e a fila do disco pode ter trabalho de ontem esperando.
        var writes = new WriteQueue(sync.ApplyAsync, _writeStore!);
        _writes = writes;

        // Escrita que subiu troca a linha provisória pela de verdade — só as tarefas, sem pagar
        // uma rodada completa de calendário e Gmail.
        writes.Drained += (_, _) => Dispatcher.InvokeAsync(() => _ = sync.RefreshTasksAsync());
        writes.Changed += (_, _) => Dispatcher.InvokeAsync(() => RefreshTasks(surface));

        // O sync roda fora da thread de UI; tudo que toca a barra volta pelo dispatcher.
        sync.Updated += (_, snapshot) => Dispatcher.InvokeAsync(() =>
        {
            _snapshot = snapshot;

            // Antes de desenhar: o retrato novo pode já refletir o que estava na fila, e nesse
            // caso a intenção deixa de existir. É onde o servidor volta a ser a única verdade.
            writes.Confirm(snapshot.Tasks);

            Rerender();
            surface.RefreshOpenPanel(
                CurrentTasks(), AllDays(snapshot),
                PlanBreaks(snapshot.Agenda, DateTimeOffset.Now), _workDay, CurrentPauta());
        });

        // "Eu vi." Suprime a ocorrência e volta ao normal na hora (regra 2, invariante I3).
        surface.Acknowledged += (_, _) => AcknowledgeCurrent();

        // SEVERITY 8: o usuario declara qual reuniao e a dele, e a partir dai so ela alimenta os
        // sinais do 2.1. E a mesma logica do D-006 — o app pergunta em vez de inferir.
        surface.ActiveEventChosen += (_, id) =>
        {
            _activeEventId = id;
            Rerender();
        };

        surface.ReauthRequested += (_, _) => OnReauthRequested();
        surface.SyncRetryRequested += (_, _) => _ = sync.RefreshAsync();
        surface.SyncRequested += (_, _) => _ = sync.RefreshAsync();
        // Abrir um painel força uma rodada: é o momento em que o usuário está de fato olhando os
        // dados, e vale a requisição extra para ele nunca ver uma lista velha.
        surface.TasksRequested += (_, _) =>
        {
            surface.ToggleTasks(CurrentTasks());
            _ = sync.RefreshAsync();
        };
        surface.AgendaRequested += (_, _) =>
        {
            surface.ToggleAgenda(
                AllDays(_snapshot), PlanBreaks(_snapshot.Agenda, DateTimeOffset.Now), _workDay,
                CurrentPauta());
            _ = sync.RefreshAsync();
        };

        // Os três gestos de escrita agora só registram a intenção. Quem fala com o Google é a
        // fila, que repete sozinha e conta o que deu errado — antes o clique ia direto para a
        // rede e sumia junto com ela.
        surface.TaskCreated += (_, title) => Enqueue(surface, PendingWrite.For(
            WriteKind.Create, DateTimeOffset.Now) with
        {
            Title = title,
            ListId = _snapshot.DefaultTaskListId,
        });

        surface.TaskToggled += (_, id) => EnqueueFor(surface, WriteKind.Complete, id);
        surface.TaskDeleted += (_, id) => EnqueueFor(surface, WriteKind.Delete, id);
        surface.TaskReopened += (_, id) => EnqueueFor(surface, WriteKind.Reopen, id);

        surface.TaskRescheduled += (_, e) =>
            EnqueueFor(surface, WriteKind.Reschedule, e.Id, w => w with { Due = e.Due });

        surface.TaskRenamed += (_, e) =>
            EnqueueFor(surface, WriteKind.Rename, e.Id, w => w with { Title = e.Title });

        surface.TaskWriteRetried += (_, id) => writes.Retry(id);
        surface.TaskWriteDiscarded += (_, id) => writes.Discard(id);

        // ------------------------------------------------------------ pauta (D-052)

        surface.PautaRequested += (_, id) =>
        {
            if (AllDays(_snapshot).FirstOrDefault(a => a.Id == id) is not { } meeting) return;

            surface.TogglePauta(meeting, Domain.Pauta.RowsFor(CurrentPauta(), id));
        };

        surface.PautaItemCreated += (_, e) =>
        {
            var meeting = AllDays(_snapshot).FirstOrDefault(a => a.Id == e.EventId);
            if (meeting is null) return;

            Enqueue(surface, PendingWrite.For(WriteKind.Create, DateTimeOffset.Now) with
            {
                Title = e.Text,
                Notes = Domain.Pauta.NotesFor(e.EventId),

                // Nulo na primeiríssima vez: é o sinal de que a lista ainda não existe no Google, e
                // quem a cria é o executor, no momento em que a escrita sai (D-052).
                ListId = _snapshot.PautaListId,
                IsPauta = true,

                // A data da reunião, só para o app do Tasks no celular agrupar no dia certo. O
                // Tempus não lê isto de volta — quem manda no vínculo são as notas.
                Due = DateOnly.FromDateTime(meeting.Start.ToLocalTime().Date),
            });
        };

        // Um gesto só na tela, dois verbos na API: qual deles depende de o assunto já estar riscado.
        surface.PautaItemToggled += (_, id) =>
        {
            if (_snapshot.Pauta.FirstOrDefault(t => t.Id == id) is not { } item) return;

            EnqueueFor(surface, item.IsCompleted ? WriteKind.Reopen : WriteKind.Complete, id);
        };

        surface.PautaItemDeleted += (_, id) => EnqueueFor(surface, WriteKind.Delete, id);

        surface.Render(BuildState(_snapshot));
        sync.Start();
        writes.Start();
    }

    /// <summary>
    /// O que a tela mostra: o retrato do servidor com as intenções pendentes por cima.
    /// <para>
    /// Um lugar só, alimentando a barra <b>e</b> o painel. Se cada um projetasse por conta, o chip
    /// diria "2 tarefas abertas" enquanto a lista logo acima mostrasse uma.
    /// </para>
    /// </summary>
    private IReadOnlyList<TaskRow> CurrentTasks() =>
        TaskProjection.Apply(_snapshot.Tasks, _writes?.Pending ?? []);

    /// <summary>
    /// Os assuntos de pauta, com as intenções pendentes por cima — a mesma projeção das tarefas,
    /// pela mesma razão: um assunto digitado no meio da reunião precisa aparecer na hora (D-029).
    /// </summary>
    private IReadOnlyList<TaskRow> CurrentPauta() =>
        TaskProjection.Apply(_snapshot.Pauta, _writes?.Pending ?? []);

    private void EnqueueFor(
        FloatingBarSurface surface,
        WriteKind kind,
        string id,
        Func<PendingWrite, PendingWrite>? detail = null)
    {
        // Nas duas listas: para a API, um assunto de pauta é uma tarefa como outra qualquer, e os
        // verbos que agem sobre ele são exatamente os mesmos (D-052).
        var task = _snapshot.Tasks.FirstOrDefault(t => t.Id == id)
            ?? _snapshot.Pauta.FirstOrDefault(t => t.Id == id);

        if (task is null) return;

        var write = PendingWrite.For(kind, DateTimeOffset.Now) with
        {
            TaskId = task.Id,
            ListId = task.ListId,
        };

        Enqueue(surface, detail is null ? write : detail(write));
    }

    private void Enqueue(FloatingBarSurface surface, PendingWrite write)
    {
        _writes?.Enqueue(write);
        RefreshTasks(surface);
    }

    /// <summary>Redesenha barra e painel a partir da mesma projeção.</summary>
    private void RefreshTasks(FloatingBarSurface surface)
    {
        Rerender();
        surface.RefreshOpenPanel(
            CurrentTasks(), AllDays(_snapshot),
            PlanBreaks(_snapshot.Agenda, DateTimeOffset.Now), _workDay, CurrentPauta());
    }

    /// <summary>
    /// Clique na barra cinza. Se o app nunca foi configurado, abrir a pasta de dados é mais útil
    /// que uma mensagem de erro: o usuário precisa colocar o <c>client_secret.json</c> ali.
    /// </summary>
    private void OnReauthRequested()
    {
        if (_snapshot.Health == SyncHealth.NotConfigured)
        {
            System.IO.Directory.CreateDirectory(GoogleOptions.DataDirectory);
            Open(GoogleOptions.DataDirectory);
            return;
        }

        _ = _sync?.ReauthorizeAsync();
    }

    /// <summary>
    /// O retrato sincronizado vira o que a barra desenha: <c>Offline</c> quando não dá para
    /// confiar nos dados (§0), e a escala 0–3 do <c>SEVERITY.md</c> quando dá.
    /// <para>
    /// As tarefas entram pela <b>projeção</b>, não pelo retrato cru: o que o usuário acabou de
    /// fazer e ainda não subiu já conta aqui, senão a barra contradiz o painel.
    /// </para>
    /// </summary>
    private ShellState BuildState(SyncSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;

        // Offline precede e ANULA a escala (§0): retorno cedo, antes de qualquer sinal. Nenhum
        // humor temporal, nenhuma pausa, nenhuma severidade — os dados não são confiáveis, e
        // avaliá-los produziria uma resposta com cara de certeza.
        if (!snapshot.IsUsable(now))
        {
            // Sem sinal avaliado não há o que anunciar. É o §0 valendo também para a interrupção:
            // offline não é "está tudo bem", é "não sabemos" — e não se acorda ninguém para isso.
            _shownSignal = null;

            return new ShellState
            {
                IsOffline = true,
                Reason = OfflineReason(snapshot, now),
                LastSyncAt = snapshot.LastSuccessAt,

                // Qual gesto a barra oferece. Sync caído não se conserta com navegador.
                OfflineNeedsConsent =
                    snapshot.Health is SyncHealth.NeedsAuth or SyncHealth.NotConfigured,
            };
        }

        var breaks = PlanBreaks(snapshot.Agenda, now);

        // Virou o dia: os reconhecimentos de ontem não valem para hoje (§7).
        var today = DateOnly.FromDateTime(now.Date);
        if (today != _acksDay)
        {
            _acksDay = today;
            _acknowledged = _acks?.Load(today) ?? [];

            // As ocorrências de fronteira do dia são identificadas pela data (§7), então guardar as
            // de ontem só faria o app calar o primeiro aviso de hoje.
            _toastsSent.Clear();
        }

        // Uma filtragem só, na entrada do domínio: as reuniões que o usuário encerrou saem do
        // humor, do evento ativo e dos sinais de uma vez (D-041). Filtrar em três lugares
        // convidaria os três a discordarem. O painel do dia segue com `snapshot.Agenda` inteira —
        // a reunião aconteceu, e o gesto não reescreve o dia.
        var agenda = MeetingLeft.Apply(snapshot.Agenda, _acknowledged);

        var time = TimeStatusResolver.Resolve(
            agenda, now, _thresholds, _workDay, _acknowledged);

        // §2 → §4 → I4/I5: avaliar os sinais, escolher um, e suavizar a descida. Três etapas
        // separadas de propósito — cada uma testável sozinha.
        var candidates = ActiveEvent.Candidates(agenda, now);

        // A escolha expira sozinha quando o evento escolhido deixa de ser candidato (SEVERITY 8:
        // "quando o evento ativo termina, reavaliar"). Sem isto, uma escolha velha calaria a
        // proxima ambiguidade do dia.
        if (_activeEventId is not null && candidates.All(c => c.Id != _activeEventId))
            _activeEventId = null;

        var tasks = CurrentTasks().Select(r => r.Item).ToList();

        var signals = Domain.Signals.Evaluate(
            agenda, tasks, now, _workDay, SignalThresholds.Default, _acknowledged,
            _dayEnded, _activeEventId);

        var winner = _gate.Apply(Arbiter.Winner(signals), now);
        _shownSignal = winner;

        // Contra a agenda inteira, e não a filtrada: é justamente a reunião que saiu dali que o
        // menu precisa poder trazer de volta.
        var encerrada = MeetingLeft.Reopenable(snapshot.Agenda, _acknowledged, now);

        // Pela projeção, e não pelo retrato cru: um assunto recém-digitado precisa contar no
        // número da barra antes de o Google confirmá-lo, como toda escrita otimista (D-029).
        var pauta = Domain.Pauta.RowsFor(CurrentPauta(), time.EventId);

        return new ShellState
        {
            Severity = winner?.Severity ?? Severity.Calm,
            Reason = winner?.Reason ?? "",
            SignalOccurrence = winner is { SelfClearing: false } ? winner.Occurrence : null,

            // D-033: o slot é o dono da narrativa de call. Quando o chip só repetiria o mesmo
            // evento, ele não é desenhado — mas continua vencendo aqui, alimentando o toast e
            // aceitando o clique de "eu vi". É supressão de pintura, não de alarme.
            ChipRepeatsTime = ChipEcho.Repeats(time.Mood, winner?.Name, time.EventId, winner?.EventId),
            ActiveEventChoices = _activeEventId is null && candidates.Count > 1 ? candidates : [],

            // O chip escala pelo mesmo critério do bloco de estado (D-025): o instante em que o
            // sinal nasceu é derivável, então basta uma subtração.
            IsEscalated =
                winner?.IsEscalatedAt(now, TimeSpan.FromMinutes(
                    Math.Max(5, _thresholds.EscalationMinutes))) == true,
            OpenTasks = tasks.Count(t => !t.IsCompleted),
            UnreadMail = snapshot.UnreadMail,
            Time = time,

            // A pauta da reunião que o humor está nomeando (D-052) — a mesma fonte que o "já saí"
            // usa, e que por isso já respeita a escolha do §8 em sobreposição. Uma reunião passada
            // não acende contador: `time.EventId` só nomeia a que está em curso ou chegando.
            PautaEventId = time.EventId,
            PautaTotal = pauta.Count,
            PautaPending = pauta.Count(p => !p.Item.IsCompleted),

            // Reconhecível quando há alarme em qualquer um dos dois vocabulários (§0.5). Sinais
            // que se limpam sozinhos ficam de fora: oferecer gesto para algo que já vai passar
            // gasta a atenção do usuário sem lhe dar poder nenhum.
            CanAcknowledge = time.Occurrence is not null || winner is { SelfClearing: false },

            // "Já saí" é irmão do "eu vi", não sinônimo (D-041). O encerrável sai da agenda
            // filtrada, porque encerrar duas vezes a mesma reunião não é gesto; o reabrível sai da
            // agenda inteira, que é onde ela continua existindo.
            LeavableOccurrence = MeetingLeft.Leavable(agenda, time, now) is { } corrente
                ? MeetingLeft.OccurrenceFor(corrente)
                : null,
            ReopenableTitle = encerrada?.Title,
            ReopenableOccurrence = encerrada is null ? null : MeetingLeft.OccurrenceFor(encerrada),
            Boundary = WorkDayResolver.Resolve(now, _workDay),
            Lookahead = Domain.Lookahead.Describe(snapshot.Upcoming, now),
            Breaks = breaks,
            BreaksEnabled = _breaks.Enabled,
            BreaksDismissed = BreaksEnabledButDismissed(now),
            BreaksTaken = BreakState(now).Taken,
            LastSyncAt = snapshot.LastSuccessAt,
        };
    }

    /// <summary>
    /// Reaplica as escolhas do usuário sobre os padrões de fábrica. Cada campo ausente no arquivo
    /// do usuário mantém o padrão — ver <see cref="UserSettings"/> sobre o porquê dos anuláveis.
    /// </summary>
    private void ApplyUserSettings()
    {
        var workDayDefaults = ThresholdsLoader.LoadWorkDay(_settingsPath);
        var breakDefaults = ThresholdsLoader.LoadBreaks(_settingsPath);

        _workDay = _user.WorkDay?.ApplyTo(workDayDefaults) ?? workDayDefaults;
        _breaks = _user.Breaks?.ApplyTo(breakDefaults) ?? breakDefaults;
    }

    /// <summary>
    /// Abre a tela de configuração. Devolve <c>false</c> só quando a primeira execução foi fechada
    /// sem preencher — o único caso em que o app não tem como seguir.
    /// </summary>
    private bool ShowSettings(bool isFirstRun)
    {
        var window = new SettingsWindow(
            Palette.FromSystemTheme(), _user.LoginHint, _workDay, _breaks, isFirstRun);

        window.ShowDialog();

        if (window.Result is not { } saved) return !window.ShouldExit;

        _user = saved;
        _userStore!.Save(saved);
        ApplyUserSettings();

        // Mudança de expediente ou de pausa vale na hora; trocar o e-mail só afeta o próximo
        // consent, então não há o que reiniciar.
        Rerender();
        return true;
    }

    /// <summary>
    /// As pausas de hoje, ou vazio se a funcionalidade está desligada ou a folga foi dispensada.
    /// A dispensa mora aqui, e não no <see cref="BreakPlanner"/>, para o planejador continuar puro.
    /// </summary>
    private IReadOnlyList<BreakSlot> PlanBreaks(IReadOnlyList<AgendaItem> agenda, DateTimeOffset now)
    {
        if (BreaksEnabledButDismissed(now)) return [];

        var state = BreakState(now);
        var planned = BreakPlanner.Plan(agenda, now, _workDay, _breaks, state.Floors());

        if (state.Started.Count == 0) return planned;

        // Pausa começada à mão manda no período: substitui a planejada, inclusive quando a
        // planejada já passou — que é justamente quando este gesto serve.
        var duration = TimeSpan.FromMinutes(Math.Max(1, _breaks.DurationMinutes));

        var manual = state.Started.Select(s => new BreakSlot
        {
            Period = s.Period,
            Start = s.At,
            End = s.At + duration,
        });

        return [.. planned.Where(p => state.Started.All(s => s.Period != p.Period))
                          .Concat(manual)
                          .OrderBy(p => p.Start)];
    }

    private bool BreaksEnabledButDismissed(DateTimeOffset now) =>
        _breaks.Enabled && BreakState(now).Dismissed;

    /// <summary>
    /// O texto do estado <c>Offline</c> (§0). Distingue os dois motivos, porque a ação é diferente:
    /// login expirado se resolve com um clique, sync parado costuma ser rede e resolve sozinho.
    /// <para>
    /// A idade do último sync é <b>calculada agora</b>, não guardada na mensagem: uma string fixa
    /// gravada no momento da falha diria "há 1 min" duas horas depois — dado velho com cara de
    /// atual, que é o que a regra 10 proíbe.
    /// </para>
    /// </summary>
    private static string OfflineReason(SyncSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Health is SyncHealth.NeedsAuth or SyncHealth.NotConfigured)
            return snapshot.Message ?? "Login do Google expirou — clique para entrar";

        if (snapshot.LastSuccessAt is not { } last) return "Sem sincronizar — clique para entrar";

        var minutes = (int)Math.Floor((now - last).TotalMinutes);

        return minutes < 60
            ? $"Sem sincronizar há {Math.Max(1, minutes)} min"
            : $"Sem sincronizar desde {last.ToLocalTime():HH:mm}";
    }

    private static DateTimeOffset AtToday(DateTimeOffset now, int hour, int minute) =>
        new(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);

    private BreakDayState BreakState(DateTimeOffset now) =>
        _dismissals?.Load(DateOnly.FromDateTime(now.Date)) ?? BreakDayState.Empty;

    private void UpdateBreakState(Func<BreakDayState, BreakDayState> change)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        _dismissals!.Save(today, change(_dismissals.Load(today)));
        Rerender();
    }

    private void Rerender()
    {
        if (_demo is not null) return; // o modo demo tem seu próprio ritmo

        var state = BuildState(_snapshot);
        _surface?.Render(state);

        // Depois de desenhar, nunca antes: a barra é a superfície principal e o toast é o eco. Se
        // as duas coisas discordassem por um instante, que a discordância caia do lado de quem
        // ainda não interrompeu ninguém.
        Announce(state, DateTimeOffset.Now);
    }

    /// <summary>
    /// A interrupção do §5, se houver. A decisão é do <see cref="ToastPolicy"/>; aqui só se junta
    /// o que ele não tem como saber — se há apresentação em curso e o que já foi emitido.
    /// </summary>
    private void Announce(ShellState state, DateTimeOffset now)
    {
        if (_toasts is not { IsAvailable: true }) return;

        Retire(state);

        var pedido = ToastPolicy.Decide(
            _shownSignal,
            state.Time,
            MeetingOf(state.Time),
            now,
            ForegroundState.ShouldYieldScreen(),
            _toastsSent,
            _toastOptions);

        if (pedido is null) return;

        _toastsSent.UnionWith(pedido.Consumes);
        _standing[pedido.Tag] = pedido.Occurrence;
        _toasts.Show(pedido);
    }

    /// <summary>
    /// "Eu vi": suprime a ocorrência e volta ao normal na hora (regra 2, invariante I3).
    /// <para>
    /// Tem nome próprio, e não é mais um lambda no fio da barra, porque agora <b>duas</b>
    /// superfícies fazem o mesmo gesto — o clique na barra e o botão do toast (D-039). Se cada uma
    /// tivesse a sua cópia, elas divergiriam no primeiro conserto.
    /// </para>
    /// </summary>
    private void AcknowledgeCurrent()
    {
        var state = BuildState(_snapshot);

        // Reconhece as duas coisas que podem estar alarmando: o humor temporal no bloco de
        // estado e o sinal no chip. São vocabulários diferentes (§0.5) e podem estar acesos ao
        // mesmo tempo — "eu vi" cala os dois, senão o clique resolveria metade.
        var seen = new[] { state.Time.Occurrence, state.SignalOccurrence }
            .Where(o => o is not null)
            .ToList();

        if (seen.Count == 0) return;

        foreach (var occurrence in seen)
        {
            _acknowledged.Add(occurrence!);

            // "Eu vi" apaga a cor e o eco junto. Deixar o aviso na Central depois de resolvido
            // é mostrar dado velho com cara de atual — a regra 10 vale para a notificação
            // também, não só para os contadores.
            var tag = ToastPolicy.TagFor(occurrence!);
            _toasts?.Withdraw(tag);
            _standing.Remove(tag);
        }

        _acks!.Save(_acksDay, _acknowledged);

        // I4 não se aplica ao reconhecimento: a descida é imediata.
        _gate.Reset(DateTimeOffset.Now);
        Rerender();
    }

    /// <summary>
    /// "Já saí desta reunião" e o desfazer, no mesmo gesto (D-041).
    /// <para>
    /// Alterna pelo mesmo motivo do gesto da pausa: um clique só, que grava em disco e vale o
    /// resto do dia, precisa de caminho de volta — senão o erro dura até amanhã.
    /// </para>
    /// <para>
    /// Não passa pela <c>WriteQueue</c>, e não deveria: nada disto sobe para o Google. A reunião
    /// continua no calendário para todo mundo; o que mudou é só o que o <b>Tempus</b> cobra de
    /// você. Por isso também não há o que falhar em silêncio, e a regra 12 não se aplica.
    /// </para>
    /// </summary>
    private void ToggleMeetingLeft(string occurrence)
    {
        // O demo tem agenda e estado próprios, e não passa pelo BuildState.
        if (_demo is not null)
        {
            _surface?.Render(_demo.ToggleLeft(occurrence));
            return;
        }

        if (!_acknowledged.Remove(occurrence)) _acknowledged.Add(occurrence);

        _acks?.Save(_acksDay, _acknowledged);

        // A descida é imediata, como no reconhecimento: I4 suaviza oscilação de dado, não gesto.
        _gate.Reset(DateTimeOffset.Now);
        Rerender();
    }

    /// <summary>
    /// A agenda inteira que o retrato carrega — hoje <b>e</b> os próximos dias (D-043).
    /// <para>
    /// O retrato separa os dois porque o humor temporal só olha hoje e o lookahead só olha o
    /// resto. O painel S3 precisa dos dois juntos, e juntar aqui evita que ele saiba dessa divisão.
    /// </para>
    /// </summary>
    private static IReadOnlyList<AgendaItem> AllDays(SyncSnapshot snapshot) =>
        [.. snapshot.Agenda, .. snapshot.Upcoming];

    /// <summary>
    /// O evento de que o humor temporal está falando. O <see cref="TimeStatus"/> guarda o id
    /// explícito justamente para esta busca não precisar fatiar a ocorrência.
    /// </summary>
    private AgendaItem? MeetingOf(TimeStatus time) =>
        time.EventId is null ? null : _snapshot.Agenda.FirstOrDefault(e => e.Id == time.EventId);

    /// <summary>
    /// Tira da tela o aviso fixo cuja situação acabou sozinha (D-039).
    /// <para>
    /// Sem isto, um aviso de reunião que ninguém tocou ficaria pendurado depois de ela terminar —
    /// dado velho com cara de atual, que é o que a regra 10 proíbe. Os três caminhos de saída são
    /// o botão, o clique na barra (que já chama <c>Withdraw</c>) e este aqui.
    /// </para>
    /// </summary>
    private void Retire(ShellState state)
    {
        if (_standing.Count == 0) return;

        // Continua de pé o que ainda é assunto: o alarme que a barra exibe, e a reunião de que o
        // humor fala. Qualquer outra coisa que tenhamos emitido já passou.
        var current = MeetingOf(state.Time) is { } meeting
            ? new[] { state.SignalOccurrence, ToastPolicy.SubjectOf(meeting) }
            : [state.SignalOccurrence];

        foreach (var (tag, occurrence) in _standing.ToList())
        {
            if (current.Contains(occurrence)) continue;

            _toasts?.Withdraw(tag);
            _standing.Remove(tag);
        }
    }

    /// <summary>
    /// Um botão do toast, ou o corpo dele. Chega de uma thread do WinRT — daí o dispatcher.
    /// <para>
    /// O contrato é o do D-038, o mesmo do clique na barra: reconhecer ganha de tudo, e onde não há
    /// o que reconhecer o gesto abre a agenda do dia. Duas superfícies, uma regra.
    /// </para>
    /// </summary>
    private void OnToastActivated(string argument) => Dispatcher.InvokeAsync(() =>
    {
        if (argument.StartsWith("ack|", StringComparison.Ordinal))
        {
            AcknowledgeCurrent();
            return;
        }

        if (BuildState(_snapshot).CanAcknowledge)
        {
            AcknowledgeCurrent();
            return;
        }

        if (_surface is FloatingBarSurface surface)
        {
            surface.ToggleAgenda(
                AllDays(_snapshot), PlanBreaks(_snapshot.Agenda, DateTimeOffset.Now), _workDay,
                CurrentPauta());
        }
    });

    /// <summary>
    /// <c>--toast-probe</c>: emite um toast de exemplo e diz o que aconteceu.
    /// <para>
    /// Existe porque a falha típica desse caminho é <b>silenciosa</b> — sem o atalho com o AUMID o
    /// Windows aceita a chamada e não mostra nada. Vale principalmente ao instalar em outra máquina
    /// (o app roda em duas): confirma a entrega sem esperar um nível 3 de verdade acontecer.
    /// </para>
    /// </summary>
    private static void RunToastProbe()
    {
        var channel = new ToastChannel();
        var opened = channel.Open();

        if (!opened)
        {
            MessageBox.Show(
                "Não foi possível abrir o canal.\n\nNormalmente é o atalho do Menu Iniciar: "
                + "rode scripts\\install.ps1 ou crie o atalho manualmente."
                + $"\n\nMotivo: {channel.LastError ?? "desconhecido"}",
                "Tempus — sonda de notificação",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        var voltas = new List<string>();
        channel.Activated += (_, argument) =>
            voltas.Add(argument.Length == 0 ? "(corpo, sem argumento)" : argument);

        channel.Show(new ToastRequest
        {
            Title = "Sonda do Tempus",

            // Um clique só: qualquer botão dispensa o toast. Então a sonda pede o único que
            // realmente está em dúvida — os outros dois são resolvidos pelo Windows.
            Body = "Clique em \"Eu vi\" para medir se o clique volta ao aplicativo.",
            Kind = ToastKind.MeetingStanding,
            Tag = "probe",
            Occurrence = "probe|sonda",
            Consumes = [],
            StaysOnScreen = true,
            Actions =
            [
                new ToastAction("Eu vi", ToastActionKind.Acknowledge, "probe|sonda"),
                new ToastAction("Dispensar", ToastActionKind.Dismiss),
            ],
        });

        // A sonda precisa continuar viva para o clique chegar — é exatamente essa dependência que
        // ela existe para medir. Bombeia mensagens em vez de dormir: sem isso o WinRT não entrega.
        var prazo = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < prazo && voltas.Count == 0)
        {
            Current.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(100);
        }

        channel.Withdraw("probe");

        // Também em arquivo, no padrão das outras sondas: o MessageBox responde a quem está na
        // frente da tela, e o arquivo responde a quem está lendo o diagnóstico depois.
        try
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "tempus-toast-probe.txt"),
                voltas.Count > 0
                    ? $"ativacao=OK\nargumentos={string.Join(" | ", voltas)}\n"
                    : "ativacao=NAO CHEGOU\n");
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Relatório da sonda não gravado: {e.Message}");
        }

        MessageBox.Show(
            voltas.Count > 0
                ? "Canal aberto, aviso entregue e o clique VOLTOU ao processo.\n\n"
                  + $"Argumentos recebidos: {string.Join(" · ", voltas)}\n\n"
                  + "É o que o botão \"Eu vi\" precisa para funcionar."
                : "Canal aberto e aviso entregue, mas nenhum clique voltou em 60 s.\n\n"
                  + "Se você clicou e nada chegou, a ativação em primeiro plano não funciona nesta "
                  + "máquina: \"Entrar na call\" e \"Dispensar\" continuam valendo (são resolvidos "
                  + "pelo Windows), mas o \"Eu vi\" precisa do plano B do D-039.\n\n"
                  + "Se você não clicou, rode de novo e clique.",
            "Tempus — sonda de notificação",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// <c>--pauta-probe</c>: a pauta ponta a ponta contra a conta real, desfazendo o que criou
    /// (D-052). Escreve o relatório em arquivo <b>e</b> em caixa, como as outras sondas.
    /// </summary>
    private async Task RunPautaProbeAsync()
    {
        var settingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var user = new UserSettingsStore(GoogleOptions.DataDirectory).Load();

        var auth = new GoogleAuth(
            GoogleOptions.ClientSecretPath, GoogleOptions.TokenDirectory, user.LoginHint);

        using var sync = new GoogleSync(
            auth, SyncOptions.Load(settingsPath), GoogleOptions.Load(settingsPath));

        string result;

        try
        {
            result = await sync.ProbePautaAsync();
        }
        catch (Exception ex)
        {
            result = $"{ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            await System.IO.File.WriteAllTextAsync(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tempus-pauta-probe.txt"),
                result);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Relatório da sonda não gravado: {ex.Message}");
        }

        MessageBox.Show(result, "Tempus — sonda de pauta", MessageBoxButton.OK);
        Shutdown();
    }

    /// <summary>
    /// <c>--dump-agenda</c>: quais calendários foram lidos e o que veio de cada um (D-034).
    /// </summary>
    private async Task RunAgendaDumpAsync()
    {
        var settingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var user = new UserSettingsStore(GoogleOptions.DataDirectory).Load();

        var auth = new GoogleAuth(
            GoogleOptions.ClientSecretPath, GoogleOptions.TokenDirectory, user.LoginHint);

        using var sync = new GoogleSync(
            auth, SyncOptions.Load(settingsPath), GoogleOptions.Load(settingsPath));

        var target = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "tempus-agenda-dump.txt");

        string result;

        try
        {
            result = await sync.DumpAgendaAsync(target);
        }
        catch (Exception ex)
        {
            result = $"{ex.GetType().Name}: {ex.Message}";
        }

        MessageBox.Show(result, "Tempus — despejo de agenda", MessageBoxButton.OK);
        Shutdown();
    }

    /// <summary>
    /// <c>--dump-tasks</c>: despeja o que o Google devolveu num arquivo e sai. Diagnóstico para
    /// separar "o Google não mandou" de "o Tempus não desenhou".
    /// </summary>
    private async Task RunTaskDumpAsync()
    {
        var settingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var google = GoogleOptions.Load(settingsPath);
        var user = new UserSettingsStore(GoogleOptions.DataDirectory).Load();

        var auth = new GoogleAuth(
            GoogleOptions.ClientSecretPath, GoogleOptions.TokenDirectory, user.LoginHint);

        using var sync = new GoogleSync(auth, SyncOptions.Load(settingsPath), google);
        var target = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "tempus-tasks-dump.txt");

        string result;

        try
        {
            result = await sync.DumpTasksAsync(target);
        }
        catch (Exception ex)
        {
            result = $"{ex.GetType().Name}: {ex.Message}";
        }

        MessageBox.Show(result, "Tempus — despejo de tarefas", MessageBoxButton.OK);
        Shutdown();
    }

    // ---------------------------------------------------------------- demo

    /// <summary>
    /// <c>--demo</c>: percorre todos os estados visuais com dados falsos, sem tocar na rede.
    /// Serve para trabalhar na UI sem depender do OAuth.
    /// </summary>
    private void StartDemo(FloatingBarSurface surface, bool failWrites)
    {
        var demo = new FakeStateSource { FailWrites = failWrites };
        _demo = demo;

        // Fila própria, com arquivo próprio: escrita falsa não pode entrar na fila que vai subir
        // para a conta de verdade. O código exercitado, esse sim, é exatamente o mesmo.
        var writes = new WriteQueue(
            demo.ApplyAsync, new PendingWriteStore(GoogleOptions.DataDirectory, "pending-writes.demo.json"));

        _writes = writes;
        writes.Changed += (_, _) => Dispatcher.InvokeAsync(() => RenderDemo(surface, demo));

        surface.Acknowledged += (_, _) => surface.Render(demo.Acknowledge());
        surface.ReauthRequested += (_, _) => surface.Render(demo.Reconnect());
        surface.SyncRequested += (_, _) => surface.Render(demo.Advance());
        surface.TasksRequested += (_, _) => surface.ToggleTasks(DemoRows(demo));
        surface.AgendaRequested += (_, _) =>
            surface.ToggleAgenda(
                demo.Agenda, PlanBreaks(demo.Agenda, DateTimeOffset.Now), _workDay,
                DemoPauta(demo));

        surface.PautaRequested += (_, id) =>
        {
            if (demo.Agenda.FirstOrDefault(a => a.Id == id) is not { } meeting) return;

            surface.TogglePauta(meeting, Domain.Pauta.RowsFor(DemoPauta(demo), id));
        };

        surface.PautaItemCreated += (_, e) =>
        {
            var meeting = demo.Agenda.FirstOrDefault(a => a.Id == e.EventId);
            if (meeting is null) return;

            EnqueueDemo(surface, demo, PendingWrite.For(
                WriteKind.Create, DateTimeOffset.Now) with
            {
                Title = e.Text,
                Notes = Domain.Pauta.NotesFor(e.EventId),
                IsPauta = true,
                Due = DateOnly.FromDateTime(meeting.Start.ToLocalTime().Date),
            });
        };

        surface.PautaItemToggled += (_, id) =>
        {
            if (demo.Pauta.FirstOrDefault(t => t.Id == id) is not { } item) return;

            EnqueueDemo(surface, demo, PendingWrite.For(
                item.IsCompleted ? WriteKind.Reopen : WriteKind.Complete,
                DateTimeOffset.Now) with { TaskId = id });
        };

        surface.PautaItemDeleted += (_, id) => EnqueueDemo(surface, demo, PendingWrite.For(
            WriteKind.Delete, DateTimeOffset.Now) with { TaskId = id });

        surface.TaskCreated += (_, title) => EnqueueDemo(surface, demo, PendingWrite.For(
            WriteKind.Create, DateTimeOffset.Now) with { Title = title });

        surface.TaskToggled += (_, id) => EnqueueDemo(surface, demo, PendingWrite.For(
            WriteKind.Complete, DateTimeOffset.Now) with { TaskId = id });

        surface.TaskDeleted += (_, id) => EnqueueDemo(surface, demo, PendingWrite.For(
            WriteKind.Delete, DateTimeOffset.Now) with { TaskId = id });

        surface.TaskReopened += (_, id) => EnqueueDemo(surface, demo, PendingWrite.For(
            WriteKind.Reopen, DateTimeOffset.Now) with { TaskId = id });

        surface.TaskRescheduled += (_, e) => EnqueueDemo(surface, demo, PendingWrite.For(
            WriteKind.Reschedule, DateTimeOffset.Now) with { TaskId = e.Id, Due = e.Due });

        surface.TaskRenamed += (_, e) => EnqueueDemo(surface, demo, PendingWrite.For(
            WriteKind.Rename, DateTimeOffset.Now) with { TaskId = e.Id, Title = e.Title });

        surface.TaskWriteRetried += (_, id) => writes.Retry(id);
        surface.TaskWriteDiscarded += (_, id) => writes.Discard(id);

        surface.Render(demo.Current);
        writes.Start();

        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        timer.Tick += (_, _) => surface.Render(demo.Advance());
        timer.Start();
    }

    private IReadOnlyList<TaskRow> DemoRows(FakeStateSource demo) =>
        TaskProjection.Apply(demo.Tasks, _writes?.Pending ?? []);

    private IReadOnlyList<TaskRow> DemoPauta(FakeStateSource demo) =>
        TaskProjection.Apply(demo.Pauta, _writes?.Pending ?? []);

    private void EnqueueDemo(FloatingBarSurface surface, FakeStateSource demo, PendingWrite write)
    {
        _writes?.Enqueue(write);
        RenderDemo(surface, demo);
    }

    private void RenderDemo(FloatingBarSurface surface, FakeStateSource demo)
    {
        surface.Render(demo.WithPending(_writes?.Pending ?? []));
        surface.RefreshOpenPanel(
            DemoRows(demo), demo.Agenda, PlanBreaks(demo.Agenda, DateTimeOffset.Now), _workDay,
            DemoPauta(demo));
    }

    // ---------------------------------------------------------------- utilidades

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Abrir navegador ou pasta é conveniência; falhar aqui não pode derrubar a barra.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _refresh?.Stop();

        // A fila já gravou tudo em disco a cada mudança; parar aqui só encerra a bomba. O que não
        // subiu sobe na próxima abertura, que é o motivo de ela ser persistida.
        _writes?.Dispose();
        _sync?.Dispose();
        _surface?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
