using Tempus.Domain;
using Xunit;

namespace Tempus.Tests;

/// <summary>Sinais (<c>SEVERITY.md</c> §2) e arbitragem (§4).</summary>
public class SignalsTests
{
    private static readonly WorkDayOptions Work = new()
    {
        StartHour = 9,
        MiddayHour = 12,
        LunchEndHour = 13,
        EndHour = 17,
    };

    private static readonly SignalThresholds Limits = SignalThresholds.Default;
    private static readonly HashSet<string> Nada = [];

    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 8, 19, hour, minute, 0, TimeSpan.FromHours(-3));

    private static AgendaItem Meeting(string id, DateTimeOffset start, DateTimeOffset end) =>
        new() { Id = id, Title = id, Start = start, End = end };

    private static TaskItem Task(string id, DateOnly? due = null) =>
        new() { Id = id, Title = id, Due = due };

    private static IReadOnlyList<Signal> Eval(
        IReadOnlyList<AgendaItem> agenda,
        IReadOnlyList<TaskItem> tasks,
        DateTimeOffset now,
        IReadOnlySet<string>? seen = null) =>
        Signals.Evaluate(agenda, tasks, now, Work, Limits, seen ?? Nada);

    private static string[] Names(IReadOnlyList<Signal> s) => [.. s.Select(x => x.Name)];

    // ---------------------------------------------------------------- §2.1 ciclo de call

    [Theory]
    [InlineData(8, "MeetingUpcoming")]
    [InlineData(1, "MeetingImminent")]
    public void Aproximacao_produz_o_sinal_certo(int faltam, string esperado)
    {
        var inicio = At(15);
        var sinais = Eval([Meeting("daily", inicio, At(16))], [], inicio.AddMinutes(-faltam));

        Assert.Contains(esperado, Names(sinais));
    }

    [Fact]
    public void Fora_da_janela_de_dez_minutos_nao_ha_sinal_de_call()
    {
        var sinais = Eval([Meeting("daily", At(15), At(16))], [], At(14, 30));

        Assert.DoesNotContain("MeetingUpcoming", Names(sinais));
    }

    [Fact]
    public void Reuniao_em_curso_produz_MeetingStarted_em_ambar()
    {
        var sinais = Eval([Meeting("daily", At(14), At(15))], [], At(14, 20));
        var started = sinais.Single(s => s.Name == "MeetingStarted");

        Assert.Equal(Severity.Attention, started.Severity);
        Assert.Equal(SignalCategory.Call, started.Category);
    }

    [Fact]
    public void Reunioes_coladas_produzem_BackToBack()
    {
        var agenda = new[]
        {
            Meeting("refino", At(14), At(15)),
            Meeting("review", At(15), At(16)),
        };

        Assert.Contains("MeetingBackToBack", Names(Eval(agenda, [], At(14, 30))));
    }

    /// <summary>Reunião isolada que acaba não cobra clique: âmbar breve que passa (§2.1).</summary>
    [Fact]
    public void MeetingEnded_e_passageiro_e_se_limpa_sozinho()
    {
        var agenda = new[] { Meeting("daily", At(14), At(15)) };

        var dentro = Eval(agenda, [], At(15, 2));
        var ended = dentro.Single(s => s.Name == "MeetingEnded");
        Assert.True(ended.SelfClearing);

        // Passados os 3 min da janela, some sozinho.
        Assert.DoesNotContain("MeetingEnded", Names(Eval(agenda, [], At(15, 5))));
    }

    /// <summary>O vermelho principal do produto: segurar uma call por cima da próxima.</summary>
    [Fact]
    public void MeetingRanIntoNext_e_critico_e_nao_se_limpa()
    {
        var agenda = new[]
        {
            Meeting("daily", At(14), At(15)),
            Meeting("review", At(15), At(16)),
        };

        var invasao = Eval(agenda, [], At(15, 3)).Single(s => s.Name == "MeetingRanIntoNext");

        Assert.Equal(Severity.Critical, invasao.Severity);
        Assert.False(invasao.SelfClearing);
        Assert.Equal(At(15), invasao.Since);
    }

    [Fact]
    public void Reuniao_de_dia_inteiro_nao_gera_sinal()
    {
        var marcador = new AgendaItem
        {
            Id = "ferias", Title = "Férias", Start = At(0), End = At(23, 59), IsAllDay = true,
        };

        Assert.Empty(Eval([marcador], [], At(10)));
    }

    // ---------------------------------------------------------------- §2.2 fronteiras

    /// <summary>Hoje = 19/08 nos horários deste arquivo.</summary>
    private static readonly DateOnly Hoje = new(2026, 8, 19);

    [Fact]
    public void Meio_dia_com_tarefas_e_ambar_nunca_vermelho()
    {
        var sinais = Eval([], [Task("t1", Hoje), Task("t2", Hoje)], At(12, 10));
        var midday = sinais.Single(s => s.Name == "MiddayCheckpoint");

        Assert.Equal(Severity.Attention, midday.Severity);
        Assert.Contains("2 tarefas abertas", midday.Reason);
    }

    /// <summary>
    /// O alarme falso que trocou o padrão do <c>CountMode</c> (D-036): duas tarefas que venciam
    /// <b>amanhã</b> faziam o meio-dia gritar em âmbar enquanto uma reunião a 15 min ficava em
    /// texto apagado ao lado. A pergunta do checkpoint é "estou em dia <i>hoje</i>?", e tarefa de
    /// amanhã não é resposta para ela.
    /// </summary>
    [Fact]
    public void Tarefa_que_vence_amanha_nao_faz_o_meio_dia_alarmar()
    {
        var amanha = Hoje.AddDays(1);
        var sinais = Eval([], [Task("t1", amanha), Task("t2", amanha)], At(12, 10));

        var midday = sinais.Single(s => s.Name == "MiddayCheckpoint");

        Assert.Equal(Severity.Info, midday.Severity);
        Assert.True(midday.SelfClearing);
    }

    /// <summary>
    /// A contrapartida da escolha, escrita para ninguém redescobrir como bug: tarefa sem
    /// vencimento <b>não conta</b> em nenhuma das duas fronteiras do dia.
    /// </summary>
    [Fact]
    public void Tarefa_sem_data_nao_conta_nas_fronteiras_do_dia()
    {
        Assert.Equal(
            Severity.Info,
            Eval([], [Task("t1"), Task("t2")], At(12, 10)).Single(s => s.Name == "MiddayCheckpoint").Severity);

        Assert.Equal(
            Severity.Info,
            Eval([], [Task("t1"), Task("t2")], At(17, 5)).Single(s => s.Name == "DayEnded").Severity);
    }

    [Fact]
    public void Meio_dia_sem_tarefas_apenas_informa_e_passa()
    {
        var limpo = Eval([], [], At(12, 5)).Single(s => s.Name == "MiddayCheckpoint");

        Assert.Equal(Severity.Info, limpo.Severity);
        Assert.True(limpo.SelfClearing);

        Assert.DoesNotContain("MiddayCheckpoint", Names(Eval([], [], At(12, 30))));
    }

    [Fact]
    public void Fim_de_jornada_com_tarefas_e_critico_e_nasce_as_17h()
    {
        var fim = Eval([], [Task("t1", Hoje), Task("t2", Hoje), Task("t3", Hoje)], At(17, 5))
            .Single(s => s.Name == "DayEnded");

        Assert.Equal(Severity.Critical, fim.Severity);
        Assert.False(fim.SelfClearing);
        Assert.Equal(At(17), fim.Since);
        Assert.Contains("3 tarefas abertas", fim.Reason);
    }

    [Fact]
    public void Fim_de_jornada_limpo_nao_alarma()
    {
        var fim = Eval([], [], At(17, 5)).Single(s => s.Name == "DayEnded");

        Assert.Equal(Severity.Info, fim.Severity);
    }

    [Fact]
    public void Fora_de_dia_util_as_fronteiras_ficam_desligadas()
    {
        var sabado = new DateTimeOffset(2026, 8, 22, 17, 5, 0, TimeSpan.FromHours(-3));

        Assert.Empty(Eval([], [Task("t1")], sabado));
    }

    // ---------------------------------------------------------------- §2.3 e §2.4

    [Fact]
    public void Tarefa_vencida_e_ambar_na_categoria_de_tarefas()
    {
        var vencida = Task("atrasada", new DateOnly(2026, 8, 17));
        var overdue = Eval([], [vencida], At(10)).Single(s => s.Name == "TaskOverdue");

        Assert.Equal(Severity.Attention, overdue.Severity);
        Assert.Equal(SignalCategory.Tasks, overdue.Category);
    }

    /// <summary>§2.4: e-mail não lido nunca colore a barra, em nenhum nível.</summary>
    [Fact]
    public void Email_nunca_gera_sinal()
    {
        // A contagem de e-mail nem entra em Evaluate — não há como ela virar cor.
        Assert.DoesNotContain(SignalCategory.Mail, Eval([], [], At(10)).Select(s => s.Category));
    }

    // ---------------------------------------------------------------- supressão

    [Fact]
    public void Sinal_reconhecido_nao_chega_a_arbitrar()
    {
        var agenda = new[] { Meeting("daily", At(14), At(15)) };
        var started = Eval(agenda, [], At(14, 20)).Single(s => s.Name == "MeetingStarted");

        var depois = Eval(agenda, [], At(14, 20), new HashSet<string> { started.Occurrence });

        Assert.DoesNotContain("MeetingStarted", Names(depois));
    }

    // ---------------------------------------------------------------- §4 arbitragem

    [Fact]
    public void Vence_a_maior_severidade()
    {
        var agenda = new[]
        {
            Meeting("daily", At(14), At(15)),
            Meeting("review", At(15), At(16)),
        };

        var vencedor = Arbiter.Winner(Eval(agenda, [Task("t1")], At(15, 3)))!;

        Assert.Equal(Severity.Critical, vencedor.Severity);
        Assert.Equal("MeetingRanIntoNext", vencedor.Name);
    }

    /// <summary>
    /// O desempate do §4: empatados em âmbar, o ciclo de call vence a tarefa vencida, porque
    /// reunião perdida não volta e tarefa vencida continua lá amanhã.
    /// </summary>
    [Fact]
    public void Empate_desempata_por_categoria()
    {
        var agenda = new[] { Meeting("daily", At(14), At(15)) };
        var vencida = Task("atrasada", new DateOnly(2026, 8, 17));

        var ativos = Eval(agenda, [vencida], At(14, 20));

        // Os dois existem e são âmbar.
        Assert.Contains("MeetingStarted", Names(ativos));
        Assert.Contains("TaskOverdue", Names(ativos));

        var vencedor = Arbiter.Winner(ativos)!;
        Assert.Equal(SignalCategory.Call, vencedor.Category);
    }

    [Fact]
    public void Sem_sinal_ativo_nao_ha_vencedor() => Assert.Null(Arbiter.Winner([]));
}
