using Tempus.Domain;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// Reuniões sobrepostas e o evento ativo (<c>SEVERITY.md</c> §8, D-009).
/// <para>
/// O caso que este mecanismo existe para matar: você aceitou duas reuniões no mesmo horário, e sem
/// saber em qual está o app dispararia um <c>MeetingRanIntoNext</c> falso no fim da primeira.
/// </para>
/// </summary>
public class ActiveEventTests
{
    private static readonly WorkDayOptions Work = new()
    {
        StartHour = 9, MiddayHour = 12, LunchEndHour = 13, EndHour = 17,
    };

    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 8, 19, hour, minute, 0, TimeSpan.FromHours(-3));

    private static AgendaItem Meeting(
        string id, DateTimeOffset start, DateTimeOffset end, Rsvp rsvp = Rsvp.None) =>
        new() { Id = id, Title = id, Start = start, End = end, Rsvp = rsvp };

    private static IReadOnlyList<Signal> Eval(
        IReadOnlyList<AgendaItem> agenda, DateTimeOffset now, string? ativo = null) =>
        Signals.Evaluate(
            agenda, [], now, Work, SignalThresholds.Default, new HashSet<string>(),
            DayEndedOptions.Default, ativo);

    // ---------------------------------------------------------------- padrão determinístico

    [Fact]
    public void Uma_reuniao_so_nao_e_ambigua()
    {
        var sinais = Eval([Meeting("daily", At(14), At(15))], At(14, 30));

        Assert.DoesNotContain("MeetingAmbiguous", sinais.Select(s => s.Name));
    }

    [Fact]
    public void Duas_sobrepostas_sem_escolha_perguntam_em_nivel_1()
    {
        var agenda = new[]
        {
            Meeting("daily", At(14), At(15)),
            Meeting("review", At(14, 30), At(15, 30)),
        };

        var pergunta = Eval(agenda, At(14, 45)).Single(s => s.Name == "MeetingAmbiguous");

        Assert.Equal(Severity.Info, pergunta.Severity);
        Assert.Contains("2 reuniões agora", pergunta.Reason);
    }

    /// <summary>§8: <c>accepted</c> vence <c>tentative</c>, que vence <c>needsAction</c>.</summary>
    [Fact]
    public void Padrao_prefere_a_que_voce_aceitou()
    {
        var candidatas = new[]
        {
            Meeting("talvez", At(14), At(15), Rsvp.Tentative),
            Meeting("aceita", At(14, 10), At(15), Rsvp.Accepted),
            Meeting("pendente", At(14, 5), At(15), Rsvp.NeedsAction),
        };

        Assert.Equal("aceita", ActiveEvent.Choose(candidatas, null)!.Id);
    }

    [Fact]
    public void Empate_de_rsvp_desempata_pelo_inicio_mais_cedo()
    {
        var candidatas = new[]
        {
            Meeting("tarde", At(14, 30), At(15), Rsvp.Accepted),
            Meeting("cedo", At(14), At(15, 30), Rsvp.Accepted),
        };

        Assert.Equal("cedo", ActiveEvent.Choose(candidatas, null)!.Id);
    }

    [Fact]
    public void Escolha_do_usuario_vence_o_padrao()
    {
        var candidatas = new[]
        {
            Meeting("aceita", At(14), At(15), Rsvp.Accepted),
            Meeting("pendente", At(14), At(15), Rsvp.NeedsAction),
        };

        Assert.Equal("pendente", ActiveEvent.Choose(candidatas, "pendente")!.Id);
    }

    /// <summary>§8: quando o evento ativo termina, reavaliar. Escolha morta não vale.</summary>
    [Fact]
    public void Escolha_que_nao_e_mais_candidata_cai_para_o_padrao()
    {
        var candidatas = new[] { Meeting("atual", At(14), At(15), Rsvp.Accepted) };

        Assert.Equal("atual", ActiveEvent.Choose(candidatas, "reuniao-de-ontem")!.Id);
    }

    // ---------------------------------------------------------------- o falso positivo

    /// <summary>
    /// O motivo de tudo isto existir. Duas reuniões aceitas em paralelo: quando a primeira termina,
    /// a segunda <b>não</b> pode contar como "próxima invadida" — ela nunca foi a próxima, era
    /// simultânea.
    /// </summary>
    [Fact]
    public void Sobreposta_nao_gera_MeetingRanIntoNext_falso()
    {
        var agenda = new[]
        {
            Meeting("daily", At(14), At(15), Rsvp.Accepted),
            Meeting("paralela", At(14, 30), At(16), Rsvp.Accepted),
        };

        // Às 15:02 a daily acabou e a paralela segue em curso. Sem o §8, isto seria vermelho.
        var sinais = Eval(agenda, At(15, 2));

        Assert.DoesNotContain("MeetingRanIntoNext", sinais.Select(s => s.Name));
    }

    /// <summary>
    /// E o contrário continua valendo: reunião que começa <b>depois</b> do fim da ativa, sem
    /// sobrepor, é sim uma próxima invadida. O §8 não pode desligar o alarme principal.
    /// </summary>
    [Fact]
    public void Sequencial_de_verdade_ainda_gera_MeetingRanIntoNext()
    {
        var agenda = new[]
        {
            Meeting("daily", At(14), At(15), Rsvp.Accepted),
            Meeting("review", At(15), At(16), Rsvp.Accepted),
        };

        var invasao = Eval(agenda, At(15, 3)).Single(s => s.Name == "MeetingRanIntoNext");

        Assert.Equal(Severity.Critical, invasao.Severity);
    }

    [Fact]
    public void Escolhida_a_reuniao_a_pergunta_some()
    {
        var agenda = new[]
        {
            Meeting("daily", At(14), At(15), Rsvp.Accepted),
            Meeting("paralela", At(14, 30), At(16), Rsvp.Accepted),
        };

        var sinais = Eval(agenda, At(14, 45), ativo: "paralela");

        Assert.DoesNotContain("MeetingAmbiguous", sinais.Select(s => s.Name));

        // E só a escolhida alimenta o ciclo: o MeetingStarted é dela.
        var started = sinais.Single(s => s.Name == "MeetingStarted");
        Assert.Contains("paralela", started.Reason);
    }
}

/// <summary>O modo de contagem do fim de jornada (D-007).</summary>
public class DayEndedCountModeTests
{
    private static readonly WorkDayOptions Work = new()
    {
        StartHour = 9, MiddayHour = 12, LunchEndHour = 13, EndHour = 17,
    };

    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 8, 19, hour, minute, 0, TimeSpan.FromHours(-3));

    private static readonly TaskItem[] Tarefas =
    [
        new() { Id = "vencida", Title = "vencida", Due = new DateOnly(2026, 8, 17) },
        new() { Id = "hoje", Title = "hoje", Due = new DateOnly(2026, 8, 19) },
        new() { Id = "semana-que-vem", Title = "futura", Due = new DateOnly(2026, 8, 26) },
        new() { Id = "sem-data", Title = "sem data" },
    ];

    private static Signal DayEnded(DayEndedCountMode mode) =>
        Signals.Evaluate(
            [], Tarefas, At(17, 5), Work, SignalThresholds.Default, new HashSet<string>(),
            new DayEndedOptions { CountMode = mode })
        .Single(s => s.Name == "DayEnded");

    [Fact]
    public void AllOpen_conta_todas_as_nao_concluidas() =>
        Assert.Contains("4 tarefas abertas", DayEnded(DayEndedCountMode.AllOpen).Reason);

    /// <summary>Backlog distante e tarefa sem data não seguram o fim do dia neste modo.</summary>
    [Fact]
    public void DueTodayOrOverdue_ignora_o_que_vence_depois() =>
        Assert.Contains("2 tarefas abertas", DayEnded(DayEndedCountMode.DueTodayOrOverdue).Reason);

    [Fact]
    public void Sem_nada_no_criterio_o_dia_fecha_limpo()
    {
        var soFuturas = new[]
        {
            new TaskItem { Id = "f", Title = "f", Due = new DateOnly(2026, 8, 26) },
        };

        var fim = Signals.Evaluate(
                [], soFuturas, At(17, 5), Work, SignalThresholds.Default, new HashSet<string>(),
                new DayEndedOptions { CountMode = DayEndedCountMode.DueTodayOrOverdue })
            .Single(s => s.Name == "DayEnded");

        Assert.Equal(Severity.Info, fim.Severity);
    }
}
