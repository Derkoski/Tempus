using Tempus.Domain;
using Xunit;

namespace Tempus.Tests;

/// <summary>Quando o Tempus tem licença para interromper (<c>SEVERITY.md</c> §5).</summary>
public class ToastPolicyTests
{
    private static readonly HashSet<string> Nada = [];

    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 8, 19, hour, minute, 0, TimeSpan.FromHours(-3));

    private static Signal Critical(
        string name = SignalNames.DayEnded,
        string occurrence = "DayEnded|2026-08-19",
        DateTimeOffset? since = null) => new()
    {
        Name = name,
        Severity = Severity.Critical,
        Reason = "Jornada encerrada: 3 tarefas abertas",
        Category = SignalCategory.DayBoundary,
        Occurrence = occurrence,
        Since = since ?? At(17),
    };

    private static ToastRequest? Decide(
        Signal? winner, DateTimeOffset now, bool suppressed = false, IReadOnlySet<string>? sent = null) =>
        ToastPolicy.Decide(winner, now, suppressed, sent ?? Nada);

    // ---------------------------------------------------------------- o que não interrompe

    /// <summary>
    /// A decisão central desta rodada: só o vermelho tem licença para achar o usuário. Âmbar e
    /// azul colorem a barra e esperam ser olhados.
    /// </summary>
    [Fact]
    public void Abaixo_do_nivel_3_nunca_interrompe()
    {
        foreach (var nivel in new[] { Severity.Calm, Severity.Info, Severity.Attention })
        {
            var sinal = Critical() with { Severity = nivel };

            Assert.Null(Decide(sinal, At(17, 1)));
        }
    }

    [Fact]
    public void Sem_vencedor_nao_interrompe() => Assert.Null(Decide(null, At(17, 1)));

    [Fact]
    public void Desligado_nas_configuracoes_nao_interrompe()
    {
        var desligado = new ToastOptions { Enabled = false };

        Assert.Null(ToastPolicy.Decide(Critical(), At(17, 1), false, Nada, desligado));
    }

    /// <summary>
    /// Nenhum crítico se limpa sozinho hoje. A cláusula existe para que um que venha a se limpar
    /// não vire interrupção por acidente — interromper por algo que sumiria em três minutos é o
    /// pior negócio do modelo.
    /// </summary>
    [Fact]
    public void Critico_passageiro_nao_interrompe()
    {
        var passageiro = Critical() with { SelfClearing = true };

        Assert.Null(Decide(passageiro, At(17, 1)));
    }

    // ---------------------------------------------------------------- as duas interrupções

    [Fact]
    public void Ao_entrar_em_vermelho_sai_um_toast_de_entrada()
    {
        var pedido = Decide(Critical(), At(17, 1))!;

        Assert.Equal(ToastKind.Entry, pedido.Kind);
        Assert.Equal("O dia não fechou", pedido.Title);
        Assert.Contains("3 tarefas abertas", pedido.Body);
        Assert.Contains("Clique na barra", pedido.Body);
    }

    [Fact]
    public void A_entrada_nao_se_repete_a_cada_tick()
    {
        var sinal = Critical();
        var primeiro = Decide(sinal, At(17, 1))!;

        // O chamador grava o que foi consumido; o tick seguinte não tem mais o que dizer.
        var depois = Decide(sinal, At(17, 2), sent: new HashSet<string>(primeiro.Consumes));

        Assert.Null(depois);
    }

    [Fact]
    public void Passados_cinco_minutos_sem_reconhecer_vem_a_segunda()
    {
        var sinal = Critical(since: At(17));
        var entrada = Decide(sinal, At(17, 1))!;
        var enviados = new HashSet<string>(entrada.Consumes);

        Assert.Null(Decide(sinal, At(17, 4), sent: enviados));

        var escalada = Decide(sinal, At(17, 6), sent: enviados)!;

        Assert.Equal(ToastKind.Escalation, escalada.Kind);
        Assert.Contains("há 6 min", escalada.Title);
    }

    [Fact]
    public void A_escalada_tambem_sai_uma_vez_so()
    {
        var sinal = Critical(since: At(17));
        var enviados = new HashSet<string> { ToastPolicy.KeyFor(sinal, ToastKind.Entry) };

        var escalada = Decide(sinal, At(17, 6), sent: enviados)!;
        enviados.UnionWith(escalada.Consumes);

        Assert.Null(Decide(sinal, At(17, 20), sent: enviados));
    }

    /// <summary>
    /// App aberto tarde, ou apresentação que durou a escalada inteira: as duas interrupções
    /// estariam vencidas ao mesmo tempo. Sai só a mais atual — duas em sequência não são dois
    /// avisos, são um susto.
    /// </summary>
    [Fact]
    public void Escalada_vencida_engole_a_entrada_que_nunca_saiu()
    {
        var sinal = Critical(since: At(17));
        var pedido = Decide(sinal, At(17, 9))!;

        Assert.Equal(ToastKind.Escalation, pedido.Kind);
        Assert.Equal(2, pedido.Consumes.Count);
        Assert.Contains(ToastPolicy.KeyFor(sinal, ToastKind.Entry), pedido.Consumes);
        Assert.Contains(ToastPolicy.KeyFor(sinal, ToastKind.Escalation), pedido.Consumes);
    }

    // ---------------------------------------------------------------- supressão

    /// <summary>
    /// §5: em apresentação a cor continua mudando e só a interrupção é retida. <b>Retida, não
    /// descartada</b> — apresentação é exatamente quando é mais fácil estourar o horário, e perder
    /// esse aviso seria perder o mais valioso.
    /// </summary>
    [Fact]
    public void Apresentacao_retem_o_toast_e_ele_sai_na_volta()
    {
        var sinal = Critical();

        Assert.Null(Decide(sinal, At(17, 1), suppressed: true));

        // Nada foi marcado como enviado durante a apresentação, então a entrada ainda vale.
        var naVolta = Decide(sinal, At(17, 3))!;

        Assert.Equal(ToastKind.Entry, naVolta.Kind);
    }

    // ---------------------------------------------------------------- identidade

    /// <summary>
    /// A deduplicação é por ocorrência (§7), não por sinal: estourar duas reuniões diferentes no
    /// mesmo dia são dois avisos, e remarcar uma reunião produz ocorrência nova de propósito.
    /// </summary>
    [Fact]
    public void Ocorrencias_diferentes_sao_toasts_diferentes()
    {
        var primeira = Critical(SignalNames.MeetingRanIntoNext, "MeetingRanIntoNext|daily|10:00");
        var segunda = Critical(SignalNames.MeetingRanIntoNext, "MeetingRanIntoNext|review|15:00");

        var enviados = new HashSet<string>(Decide(primeira, At(17, 1))!.Consumes);
        var outra = Decide(segunda, At(17, 1), sent: enviados)!;

        Assert.Equal("A reunião passou do horário", outra.Title);
    }
}
