using Tempus.Domain;
using Tempus.Shell;
using Tempus.Sync;

namespace Tempus.Fake;

/// <summary>
/// Dados falsos. Percorre um roteiro que cobre <b>todos</b> os estados visuais do
/// <c>docs/SEVERITY.md</c> e mantém uma lista de tarefas mutável, para que o laço painel→barra
/// funcione de verdade: concluir uma tarefa faz o contador da barra cair.
/// <para>
/// Continua em cena depois da Fase 3 por causa da regra do D-024: <b>escrita se verifica em demo,
/// nunca na conta real</b>. Um clique de teste 15 pixels fora do alvo já concluiu uma tarefa de
/// verdade do usuário uma vez.
/// </para>
/// <para>
/// A escrita aqui é o mesmo caminho da real: entra pela <see cref="WriteQueue"/> e chega em
/// <see cref="ApplyAsync"/>. Se fosse um atalho, verificar em demo não provaria nada sobre o
/// código que roda de verdade.
/// </para>
/// </summary>
internal sealed class FakeStateSource
{
    private const int Mail = 12;

    private readonly List<TaskItem> _tasks;
    private readonly List<AgendaItem> _agenda;
    private readonly ShellState[] _script;
    private readonly object _gate = new();

    /// <summary>As reuniões que o usuário declarou encerradas (D-041), no espelho do que o
    /// caminho real guarda no <c>AcknowledgementStore</c>.</summary>
    private readonly HashSet<string> _left;

    private IReadOnlyList<PendingWrite> _pending = [];
    private int _index;
    private int _nextId = 100;

    /// <summary>
    /// <c>--fail-writes</c>: toda escrita falha como se a rede estivesse fora. Existe para
    /// exercitar o caminho que não dá para provocar sob demanda — a linha esmaecida, as três
    /// tentativas, a pílula de "não salvou", o repetir e o descartar.
    /// </summary>
    public bool FailWrites { get; init; }

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

            // Duas já concluídas, para a seção de desfazer ter o que mostrar sem exigir que
            // alguém conclua uma antes de poder testá-la (D-030).
            new TaskItem
            {
                Id = "t7",
                Title = "Fechar o apontamento de horas",
                IsCompleted = true,
                CompletedAt = DateTimeOffset.Now.AddMinutes(-40),
            },
            new TaskItem
            {
                Id = "t8",
                Title = "Responder a pesquisa de clima",
                IsCompleted = true,
                CompletedAt = DateTimeOffset.Now.AddDays(-2),
            },
        ];

        _left = [];
        _agenda = BuildAgenda();
        _script = BuildScript();
        Current = Stamp(_script[0]);
    }

    public ShellState Current { get; private set; }

    /// <summary>Cópia: a fila escreve fora da thread de UI, que é quem lê.</summary>
    public IReadOnlyList<TaskItem> Tasks
    {
        get { lock (_gate) return [.. _tasks]; }
    }

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

    /// <summary>
    /// "Já saí desta reunião", e o desfazer (D-041).
    /// <para>
    /// O demo <b>precisa</b> deste gesto de verdade, e não só do item no menu: é o único lugar
    /// onde ele pode ser exercitado sem esperar uma call acabar cedo na conta real — e o D-038
    /// aprendeu, à custa de um alarme do usuário, que gesto se verifica no demo.
    /// </para>
    /// </summary>
    public ShellState ToggleLeft(string occurrence)
    {
        if (!_left.Remove(occurrence)) _left.Add(occurrence);

        return Current = Stamp(_script[_index]);
    }

    /// <summary>Redesenha a barra levando em conta o que ainda não subiu.</summary>
    public ShellState WithPending(IReadOnlyList<PendingWrite> pending)
    {
        _pending = pending;
        return Current = Stamp(_script[_index]);
    }

    /// <summary>O executor que a <see cref="WriteQueue"/> injeta no lugar do Google.</summary>
    public Task<WriteOutcome> ApplyAsync(PendingWrite write, CancellationToken ct)
    {
        if (FailWrites)
            return Task.FromResult(WriteOutcome.Failed(null, "Falha simulada por --fail-writes"));

        lock (_gate)
        {
            switch (write.Kind)
            {
                case WriteKind.Create:
                    _tasks.Add(new TaskItem { Id = $"t{_nextId++}", Title = write.Title ?? "" });
                    break;

                case WriteKind.Complete:
                    var i = _tasks.FindIndex(t => t.Id == write.TaskId);
                    if (i >= 0)
                        _tasks[i] = _tasks[i] with
                        {
                            IsCompleted = true,
                            CompletedAt = DateTimeOffset.Now,
                        };
                    break;

                case WriteKind.Reopen:
                    var j = _tasks.FindIndex(t => t.Id == write.TaskId);
                    if (j >= 0)
                        _tasks[j] = _tasks[j] with { IsCompleted = false, CompletedAt = null };
                    break;

                case WriteKind.Delete:
                    _tasks.RemoveAll(t => t.Id == write.TaskId);
                    break;

                case WriteKind.Reschedule:
                    var k = _tasks.FindIndex(t => t.Id == write.TaskId);
                    if (k >= 0) _tasks[k] = _tasks[k] with { Due = write.Due };
                    break;

                case WriteKind.Rename:
                    var r = _tasks.FindIndex(t => t.Id == write.TaskId);
                    if (r >= 0) _tasks[r] = _tasks[r] with { Title = write.Title ?? _tasks[r].Title };
                    break;

                // Verbo que o demo não conhece falha em voz alta, e não em silêncio.
                //
                // Encontrado em uso, e o modo de falhar é traiçoeiro: sem este ramo, um WriteKind
                // novo "sucedia" sem fazer nada, saía da fila, o efeito otimista sumia junto e a
                // linha voltava atrás. Parecia bug da funcionalidade nova, quando era o demo que
                // não a implementava — foi exatamente o que aconteceu com o Reschedule do D-042.
                //
                // Pior que perder tempo: o demo é onde as escritas se verificam (D-024), então um
                // no-op silencioso aqui pode aprovar como "verificado" algo que nunca rodou.
                default:
                    return Task.FromResult(WriteOutcome.Failed(
                        null, $"O modo demo não implementa {write.Kind}"));
            }
        }

        return Task.FromResult(WriteOutcome.Ok);
    }

    /// <summary>
    /// O contador de tarefas vem sempre da lista viva, nunca do roteiro — é isso que faz concluir
    /// uma tarefa refletir na barra. Offline apaga os contadores em vez de mostrar o último valor
    /// conhecido (<c>SEVERITY.md</c> §0, invariante I7).
    /// </summary>
    private ShellState Stamp(ShellState state)
    {
        if (state.IsOffline)
        {
            return state with
            {
                OpenTasks = null,
                UnreadMail = null,
                LastSyncAt = null,
                Time = TimeStatus.Unknown,
            };
        }

        // A mesma conta que o BuildState faz no caminho real, incluindo a filtragem do D-041.
        // Repetir a fórmula aqui faria o demo mentir justamente sobre o que ele existe para
        // exercitar — e é no demo que os gestos se verificam (D-024, D-038).
        var now = DateTimeOffset.Now;
        var visivel = MeetingLeft.Apply(_agenda, _left);
        var time = TimeStatusResolver.Resolve(
            visivel, now, TimeThresholds.Default, WorkDayOptions.Default);

        var encerravel = MeetingLeft.Leavable(visivel, time, now);
        var reabrivel = MeetingLeft.Reopenable(_agenda, _left, now);

        return state with
        {
            // Pela projeção, não pela lista crua: senão o contador da barra discordaria do painel
            // enquanto uma escrita estivesse a caminho.
            OpenTasks = TaskProjection.Apply(Tasks, _pending).Count(r => !r.Item.IsCompleted),
            UnreadMail = Mail,
            LastSyncAt = now,
            Time = time,
            LeavableOccurrence = encerravel is null ? null : MeetingLeft.OccurrenceFor(encerravel),
            ReopenableOccurrence = reabrivel is null ? null : MeetingLeft.OccurrenceFor(reabrivel),
            ReopenableTitle = reabrivel?.Title,
        };
    }

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

            // --- Os próximos dias (D-043) ---
            //
            // Sem isto o resumo apareceria com catorze linhas vazias e não exercitaria nada. Os
            // dias escolhidos cobrem as três leituras que o painel precisa acertar: um dia com
            // buraco no meio, um dia tomado de ponta a ponta, e um dia sem nada.
            new AgendaItem
            {
                Id = "f1", Title = "Planning do time",
                Start = Amanha(9), End = Amanha(11),
                Conference = meet, Rsvp = Rsvp.Accepted,
            },
            new AgendaItem
            {
                Id = "f2", Title = "Comitê de arquitetura",
                Start = Amanha(14), End = Amanha(15, 30),
                Conference = zoom, Rsvp = Rsvp.Accepted,
            },

            // Depois de amanhã sem folga nenhuma: o resumo tem de dizer "Agenda cheia" em vez de
            // uma linha vazia, que é o caso em que o usuário responderia errado.
            new AgendaItem
            {
                Id = "f3", Title = "Imersão com o cliente",
                Start = EmDias(2, 8), End = EmDias(2, 12),
                Conference = meet, Rsvp = Rsvp.Accepted,
            },
            new AgendaItem
            {
                Id = "f4", Title = "Imersão com o cliente (tarde)",
                Start = EmDias(2, 13), End = EmDias(2, 17),
                Conference = meet, Rsvp = Rsvp.Accepted,
            },

            // Daqui a três dias, uma call só de manhã — o caso comum.
            new AgendaItem
            {
                Id = "f5", Title = "Week Review",
                Start = EmDias(3, 10), End = EmDias(3, 11),
                Conference = meet, Rsvp = Rsvp.Accepted,
            },
        ];
    }

    private static DateTimeOffset Amanha(int hour, int minute = 0) => EmDias(1, hour, minute);

    private static DateTimeOffset EmDias(int days, int hour, int minute = 0)
    {
        var day = DateTime.Today.AddDays(days);

        return new DateTimeOffset(
            day.Year, day.Month, day.Day, hour, minute, 0, DateTimeOffset.Now.Offset);
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

        // O chip calado por repetição (D-033). Tem motivo e severidade — o alarme existe, vence a
        // arbitragem e aceita o clique — mas o slot já está contando a mesma reunião, então ele
        // não é desenhado. Está no roteiro porque o modo demo não passa pelo BuildState e sem isto
        // a supressão não teria como ser vista na tela.
        new ShellState
        {
            Severity = Severity.Attention,
            Reason = "Daily começou às 14:00",
            ChipRepeatsTime = true,
            CanAcknowledge = true,
        },

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
