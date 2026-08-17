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
        _workDay = ThresholdsLoader.LoadWorkDay(settingsPath);

        var surface = new FloatingBarSurface(BarOptions.Load(settingsPath));
        _surface = surface;

        surface.MailRequested += (_, _) => Open("https://mail.google.com");
        surface.MeetingActivated += (_, url) => Open(url);
        surface.ExitRequested += (_, _) => Shutdown();

        if (e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase))
            StartDemo(surface);
        else
            StartGoogle(surface, GoogleOptions.Load(settingsPath), SyncOptions.Load(settingsPath));

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
            surface.RefreshOpenPanel(snapshot.Tasks, snapshot.Agenda);
        });

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
            surface.ToggleAgenda(_snapshot.Agenda);
            _ = sync.RefreshAsync();
        };
        surface.TaskCreated += (_, title) => _ = sync.CreateTaskAsync(title);
        surface.TaskToggled += (_, id) =>
        {
            var task = _snapshot.Tasks.FirstOrDefault(t => t.Id == id);
            if (task is not null) _ = sync.CompleteTaskAsync(task);
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

        return new ShellState
        {
            Severity = Severity.Calm,
            Reason = "",
            OpenTasks = snapshot.Tasks.Count(t => !t.IsCompleted),
            UnreadMail = snapshot.UnreadMail,
            Time = TimeStatusResolver.Resolve(snapshot.Agenda, now, _thresholds, _workDay),
            Boundary = WorkDayResolver.Resolve(now, _workDay),
            Lookahead = Domain.Lookahead.Describe(snapshot.Upcoming, now),
            LastSyncAt = snapshot.LastSuccessAt,
        };
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
        surface.AgendaRequested += (_, _) => surface.ToggleAgenda(demo.Agenda);
        surface.TaskToggled += (_, id) =>
        {
            surface.Render(demo.ToggleTask(id));
            surface.RefreshOpenPanel(demo.Tasks, demo.Agenda);
        };
        surface.TaskCreated += (_, title) =>
        {
            surface.Render(demo.CreateTask(title));
            surface.RefreshOpenPanel(demo.Tasks, demo.Agenda);
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
