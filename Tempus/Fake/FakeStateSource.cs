using Tempus.Domain;
using Tempus.Shell;

namespace Tempus.Fake;

/// <summary>
/// Dados falsos para a Fase 2. Percorre um roteiro que cobre <b>todos</b> os estados visuais do
/// <c>docs/SEVERITY.md</c> e mantém uma lista de tarefas mutável, para que o laço painel→barra
/// funcione de verdade: concluir uma tarefa faz o contador da barra cair.
/// <para>
/// Existe para que as fases 1 e 2 do roadmap sejam independentes — a barra e os painéis podem ser
/// validados sem depender do OAuth. Sai de cena na Fase 3.
/// </para>
/// </summary>
internal sealed class FakeStateSource
{
    private const int Mail = 12;

    private readonly List<TaskItem> _tasks;
    private readonly List<AgendaItem> _agenda;
    private readonly ShellState[] _script;

    private int _index;
    private int _nextId = 100;

    public FakeStateSource()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        _tasks =
        [
            new TaskItem { Id = "t1", Title = "Revisar PR do módulo de faturamento", Due = today.AddDays(-1) },
            new TaskItem { Id = "t2", Title = "Responder e-mail do cliente sobre integração", Due = today.AddDays(-3) },
            new TaskItem { Id = "t3", Title = "Atualizar planilha de capacidade do time", Due = today },
            new TaskItem { Id = "t4", Title = "Preparar slides da Review de sprint", Due = today },
            new TaskItem { Id = "t5", Title = "Avaliar migração para .NET 10", Due = today.AddDays(6) },
            new TaskItem { Id = "t6", Title = "Anotar ideias para o Tempus" },
        ];

        _agenda = BuildAgenda();
        _script = BuildScript();
        Current = Stamp(_script[0]);
    }

    public ShellState Current { get; private set; }

    public IReadOnlyList<TaskItem> Tasks => _tasks;

    public IReadOnlyList<AgendaItem> Agenda => _agenda;

    public ShellState Advance()
    {
        _index = (_index + 1) % _script.Length;
        return Current = Stamp(_script[_index]);
    }

    /// <summary>Reconhecer volta ao normal imediatamente (invariante I3).</summary>
    public ShellState Acknowledge()
    {
        _index = 0;
        return Current = Stamp(_script[0]);
    }

    /// <summary>Simula o re-consent semanal bem-sucedido (<c>SEVERITY.md</c> §6).</summary>
    public ShellState Reconnect() => Acknowledge();

    public ShellState ToggleTask(string id)
    {
        var i = _tasks.FindIndex(t => t.Id == id);
        if (i >= 0) _tasks[i] = _tasks[i] with { IsCompleted = !_tasks[i].IsCompleted };

        return Current = Stamp(_script[_index]);
    }

    public ShellState CreateTask(string title)
    {
        _tasks.Add(new TaskItem { Id = $"t{_nextId++}", Title = title });
        return Current = Stamp(_script[_index]);
    }

    /// <summary>
    /// O contador de tarefas vem sempre da lista viva, nunca do roteiro — é isso que faz concluir
    /// uma tarefa refletir na barra. Offline apaga os contadores em vez de mostrar o último valor
    /// conhecido (<c>SEVERITY.md</c> §0, invariante I7).
    /// </summary>
    private ShellState Stamp(ShellState state) => state.IsOffline
        ? state with
        {
            OpenTasks = null,
            UnreadMail = null,
            LastSyncAt = null,
            Time = TimeStatus.Unknown,
        }
        : state with
        {
            OpenTasks = _tasks.Count(t => !t.IsCompleted),
            UnreadMail = Mail,
            LastSyncAt = DateTimeOffset.Now,
            Time = TimeStatusResolver.Resolve(
                _agenda, DateTimeOffset.Now, TimeThresholds.Default, WorkDayOptions.Default),
        };

    /// <summary>
    /// Agenda ancorada em <c>agora</c> arredondado, para que sempre exista um evento em curso e um
    /// próximo — e para que os casos que importam apareçam: dois "sem intervalo" e uma
    /// sobreposição, que é o cenário de <c>MeetingAmbiguous</c> (D-009).
    /// </summary>
    private static List<AgendaItem> BuildAgenda()
    {
        var now = DateTimeOffset.Now;
        var b = now.AddMinutes(-(now.Minute % 15)).AddSeconds(-now.Second).AddMilliseconds(-now.Millisecond);

        var meet = Conference.FromUrl("https://meet.google.com/abc-defg-hij");

        // O caso que motivou o D-017: convite com o link do Zoom no corpo, e não em
        // conferenceData. Passa pela mesma varredura que a agenda real usa — inclusive o &amp;
        // do HTML e o ?pwd= que não pode ser cortado.
        var zoom = Conference.FindIn(
            "<p>Entre pelo link:</p><a href=\"https://us02web.zoom.us/j/89012345678"
            + "?pwd=Zm9vYmFy&amp;from=addon\">Ingressar na reunião</a>");

        return
        [
            new AgendaItem
            {
                Id = "e1", Title = "Daily do time",
                Start = b.AddMinutes(-90), End = b.AddMinutes(-75),
                Conference = meet, Rsvp = Rsvp.Accepted,
            },
            // 45 min livre
            new AgendaItem
            {
                Id = "e2", Title = "Refinamento com produto",
                Start = b.AddMinutes(-30), End = b.AddMinutes(10),
                Conference = meet, Rsvp = Rsvp.Accepted,
            },
            // sem intervalo
            new AgendaItem
            {
                Id = "e3", Title = "1:1 com o gestor",
                Start = b.AddMinutes(10), End = b.AddMinutes(70),
                Conference = zoom, Rsvp = Rsvp.NeedsAction,
            },
            // 30 min livre
            new AgendaItem
            {
                Id = "e4", Title = "Review de sprint",
                Start = b.AddMinutes(100), End = b.AddMinutes(175),
                Conference = meet, Rsvp = Rsvp.Tentative,
            },
            // sobreposição de 15 min
            new AgendaItem
            {
                Id = "e5", Title = "Alinhamento com arquitetura",
                Start = b.AddMinutes(160), End = b.AddMinutes(190),
                Conference = zoom, Rsvp = Rsvp.NeedsAction,
            },
            // Presencial e sem convidados: sem fundo, sem marca, sem clique.
            new AgendaItem
            {
                Id = "e6", Title = "Retrospectiva",
                Start = b.AddMinutes(220), End = b.AddMinutes(250),
            },
        ];
    }

    private static ShellState[] BuildScript() =>
    [
        // Calm tem motivo VAZIO: "o que vem a seguir" agora mora no slot esquerdo (D-011), e esta
        // área é reservada ao que exige ação. Barra calma fica em branco no meio, de propósito.
        new ShellState { Severity = Severity.Calm, Reason = "" },
        new ShellState { Severity = Severity.Info, Reason = "Daily em 6 min" },
        new ShellState { Severity = Severity.Attention, Reason = "Daily começa em 1 min" },
        new ShellState
        {
            Severity = Severity.Attention,
            Reason = "Daily começou às 14:00",
            CanAcknowledge = true,
        },
        new ShellState { Severity = Severity.Info, Reason = "2 reuniões agora — qual?" },

        // O vermelho principal do produto (SEVERITY.md §2.1).
        new ShellState
        {
            Severity = Severity.Critical,
            Reason = "Daily acabou — Review já começou",
            CanAcknowledge = true,
        },
        // O mesmo alerta 5 min depois, sem reconhecimento (§1.1).
        new ShellState
        {
            Severity = Severity.Critical,
            Reason = "Daily acabou há 7 min — Review já começou",
            IsEscalated = true,
            CanAcknowledge = true,
        },
        new ShellState
        {
            Severity = Severity.Attention,
            Reason = "Metade do dia: 4 tarefas abertas",
            CanAcknowledge = true,
        },
        new ShellState
        {
            Severity = Severity.Critical,
            Reason = "Jornada encerrada: 3 tarefas abertas",
            CanAcknowledge = true,
        },
        // Offline anula a escala e apaga os contadores (§0, invariante I7).
        new ShellState { IsOffline = true, Reason = "Login expirado — clique para entrar" },
    ];
}
