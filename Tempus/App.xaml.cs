using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Tempus.Domain;
using Tempus.Fake;
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
        _dismissals = new BreakDismissalStore(GoogleOptions.DataDirectory);
        _acks = new AcknowledgementStore(GoogleOptions.DataDirectory);
        _acksDay = DateOnly.FromDateTime(DateTime.Today);
        _acknowledged = _acks.Load(_acksDay);
        _userStore = new UserSettingsStore(GoogleOptions.DataDirectory);
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

        surface.SettingsRequested += (_, _) => ShowSettings(isFirstRun: false);

        if (isDemo)
            StartDemo(surface);
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

        var sync = new GoogleSync(auth, cadence, options.MailQuery);
        _sync = sync;

        // O sync roda fora da thread de UI; tudo que toca a barra volta pelo dispatcher.
        sync.Updated += (_, snapshot) => Dispatcher.InvokeAsync(() =>
        {
            _snapshot = snapshot;
            Rerender();
            surface.RefreshOpenPanel(
                snapshot.Tasks, snapshot.Agenda, PlanBreaks(snapshot.Agenda, DateTimeOffset.Now));
        });

        // "Eu vi." Suprime a ocorrência e volta ao normal na hora (regra 2, invariante I3).
        surface.Acknowledged += (_, _) =>
        {
            if (BuildState(_snapshot).Time.Occurrence is not { } occurrence) return;

            _acknowledged.Add(occurrence);
            _acks!.Save(_acksDay, _acknowledged);
            Rerender();
        };

        surface.ReauthRequested += (_, _) => OnReauthRequested();
        surface.SyncRequested += (_, _) => _ = sync.RefreshAsync();
        // Abrir um painel força uma rodada: é o momento em que o usuário está de fato olhando os
        // dados, e vale a requisição extra para ele nunca ver uma lista velha.
        surface.TasksRequested += (_, _) =>
        {
            surface.ToggleTasks(_snapshot.Tasks);
            _ = sync.RefreshAsync();
        };
        surface.AgendaRequested += (_, _) =>
        {
            surface.ToggleAgenda(_snapshot.Agenda, PlanBreaks(_snapshot.Agenda, DateTimeOffset.Now));
            _ = sync.RefreshAsync();
        };
        surface.TaskCreated += (_, title) => _ = sync.CreateTaskAsync(title);
        surface.TaskToggled += (_, id) =>
        {
            var task = _snapshot.Tasks.FirstOrDefault(t => t.Id == id);
            if (task is not null) _ = sync.CompleteTaskAsync(task);
        };
        surface.TaskDeleted += (_, id) =>
        {
            var task = _snapshot.Tasks.FirstOrDefault(t => t.Id == id);
            if (task is not null) _ = sync.DeleteTaskAsync(task);
        };

        surface.Render(BuildState(_snapshot));
        sync.Start();
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
    /// Estado da Fase 1: dados reais, sem a máquina de severidade ainda. Só distingue "dá para
    /// confiar no que temos" de <c>Offline</c>. A escala 0–3 do <c>SEVERITY.md</c> entra na Fase 3.
    /// </summary>
    private ShellState BuildState(SyncSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;

        if (!snapshot.IsUsable(now))
        {
            return new ShellState
            {
                IsOffline = true,
                Reason = snapshot.Message ?? "Sem sincronização",
            };
        }

        var breaks = PlanBreaks(snapshot.Agenda, now);

        // Virou o dia: os reconhecimentos de ontem não valem para hoje (§7).
        var today = DateOnly.FromDateTime(now.Date);
        if (today != _acksDay)
        {
            _acksDay = today;
            _acknowledged = _acks?.Load(today) ?? [];
        }

        var time = TimeStatusResolver.Resolve(
            snapshot.Agenda, now, _thresholds, _workDay, _acknowledged);

        return new ShellState
        {
            Severity = Severity.Calm,
            Reason = "",
            OpenTasks = snapshot.Tasks.Count(t => !t.IsCompleted),
            UnreadMail = snapshot.UnreadMail,
            Time = time,
            CanAcknowledge = time.Occurrence is not null,
            Boundary = WorkDayResolver.Resolve(now, _workDay),
            Lookahead = Domain.Lookahead.Describe(snapshot.Upcoming, now),
            Breaks = breaks,
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

        return BreakPlanner.Plan(agenda, now, _workDay, _breaks, BreakState(now).Floors());
    }

    private bool BreaksEnabledButDismissed(DateTimeOffset now) =>
        _breaks.Enabled && BreakState(now).Dismissed;

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

        _surface?.Render(BuildState(_snapshot));
    }

    // ---------------------------------------------------------------- demo

    /// <summary>
    /// <c>--demo</c>: percorre todos os estados visuais com dados falsos, sem tocar na rede.
    /// Serve para trabalhar na UI sem depender do OAuth.
    /// </summary>
    private void StartDemo(FloatingBarSurface surface)
    {
        var demo = new FakeStateSource();
        _demo = demo;

        surface.Acknowledged += (_, _) => surface.Render(demo.Acknowledge());
        surface.ReauthRequested += (_, _) => surface.Render(demo.Reconnect());
        surface.SyncRequested += (_, _) => surface.Render(demo.Advance());
        surface.TasksRequested += (_, _) => surface.ToggleTasks(demo.Tasks);
        surface.AgendaRequested += (_, _) =>
            surface.ToggleAgenda(demo.Agenda, PlanBreaks(demo.Agenda, DateTimeOffset.Now));
        surface.TaskToggled += (_, id) =>
        {
            surface.Render(demo.ToggleTask(id));
            surface.RefreshOpenPanel(
                demo.Tasks, demo.Agenda, PlanBreaks(demo.Agenda, DateTimeOffset.Now));
        };
        surface.TaskCreated += (_, title) =>
        {
            surface.Render(demo.CreateTask(title));
            surface.RefreshOpenPanel(
                demo.Tasks, demo.Agenda, PlanBreaks(demo.Agenda, DateTimeOffset.Now));
        };
        surface.TaskDeleted += (_, id) =>
        {
            surface.Render(demo.DeleteTask(id));
            surface.RefreshOpenPanel(
                demo.Tasks, demo.Agenda, PlanBreaks(demo.Agenda, DateTimeOffset.Now));
        };

        surface.Render(demo.Current);

        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        timer.Tick += (_, _) => surface.Render(demo.Advance());
        timer.Start();
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
        _sync?.Dispose();
        _surface?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
