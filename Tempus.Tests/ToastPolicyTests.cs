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

    /// <summary>Humor que não fala de reunião nenhuma — isola o caminho de nível 3.</summary>
    private static readonly TimeStatus Livre = new() { Mood = TimeMood.Free, Label = "Livre" };

    private static ToastRequest? Decide(
        Signal? winner, DateTimeOffset now, bool suppressed = false, IReadOnlySet<string>? sent = null) =>
        ToastPolicy.Decide(winner, Livre, null, now, suppressed, sent ?? Nada);

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

        Assert.Null(ToastPolicy.Decide(Critical(), Livre, null, At(17, 1), false, Nada, desligado));
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
        var enviados = new HashSet<string> { ToastPolicy.KeyFor(sinal.Occurrence, ToastKind.Entry) };

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
        Assert.Contains(ToastPolicy.KeyFor(sinal.Occurrence, ToastKind.Entry), pedido.Consumes);
        Assert.Contains(ToastPolicy.KeyFor(sinal.Occurrence, ToastKind.Escalation), pedido.Consumes);
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

    // ================================================================ reunião (D-039)

    private static AgendaItem Reuniao(int hour = 13, int minute = 0, string? url = "https://meet/x") =>
        new()
        {
            Id = "backlog-pearson",
            Title = "Backlog Pearson",
            Start = At(hour, minute),
            End = At(hour + 1, minute),
            Conference = url is null
                ? null
                : new Conference { Url = url, Provider = ConferenceProvider.Meet },
        };

    /// <summary>O humor que a barra mostra enquanto a reunião se aproxima.</summary>
    private static TimeStatus Aproximando(TimeMood mood = TimeMood.Approaching) => new()
    {
        Mood = mood,
        Label = mood == TimeMood.Imminent ? "Começando" : "Em breve",
        EventId = "backlog-pearson",
    };

    private static ToastRequest? Aviso(
        DateTimeOffset now,
        TimeMood mood = TimeMood.Approaching,
        AgendaItem? meeting = null,
        IReadOnlySet<string>? sent = null,
        ToastOptions? options = null) =>
        ToastPolicy.Decide(
            null, Aproximando(mood), meeting ?? Reuniao(), now, false, sent ?? Nada, options);

    [Fact]
    public void Faltando_dez_minutos_sai_o_aviso_passageiro()
    {
        var pedido = Aviso(At(12, 50))!;

        Assert.Equal(ToastKind.MeetingHeadsUp, pedido.Kind);
        Assert.Equal("Backlog Pearson", pedido.Title);
        Assert.Contains("10 min", pedido.Body);
        Assert.False(pedido.StaysOnScreen);
    }

    [Fact]
    public void Longe_demais_ainda_nao_interrompe() => Assert.Null(Aviso(At(12, 40)));

    [Fact]
    public void Faltando_dois_minutos_sai_o_aviso_que_fica()
    {
        var enviados = new HashSet<string>(Aviso(At(12, 50))!.Consumes);
        var fixo = Aviso(At(12, 58), TimeMood.Imminent, sent: enviados)!;

        Assert.Equal(ToastKind.MeetingStanding, fixo.Kind);
        Assert.True(fixo.StaysOnScreen);
    }

    /// <summary>
    /// O fixo substitui o passageiro na Central em vez de empilhar dois avisos sobre a mesma
    /// reunião — a mesma etiqueta que a entrada e a escalada do nível 3 já compartilham.
    /// </summary>
    [Fact]
    public void Os_dois_avisos_da_mesma_reuniao_dividem_a_etiqueta()
    {
        var passageiro = Aviso(At(12, 50))!;
        var fixo = Aviso(At(12, 58), TimeMood.Imminent, sent: new HashSet<string>(passageiro.Consumes))!;

        Assert.Equal(passageiro.Tag, fixo.Tag);
    }

    [Fact]
    public void Nenhum_dos_dois_se_repete_a_cada_tick()
    {
        var enviados = new HashSet<string>();

        enviados.UnionWith(Aviso(At(12, 50), sent: enviados)!.Consumes);
        Assert.Null(Aviso(At(12, 51), sent: enviados));

        enviados.UnionWith(Aviso(At(12, 58), TimeMood.Imminent, sent: enviados)!.Consumes);
        Assert.Null(Aviso(At(12, 59), TimeMood.Imminent, sent: enviados));
    }

    /// <summary>
    /// App aberto a um minuto da reunião: o passageiro nunca saiu, e emitir os dois em sequência
    /// seria um susto. Mesma regra que a escalada do nível 3 já aplicava.
    /// </summary>
    [Fact]
    public void O_fixo_engole_o_passageiro_que_nunca_saiu()
    {
        var pedido = Aviso(At(12, 59), TimeMood.Imminent)!;

        Assert.Equal(ToastKind.MeetingStanding, pedido.Kind);
        Assert.Equal(2, pedido.Consumes.Count);
    }

    [Fact]
    public void Comecada_a_reuniao_nenhum_aviso_novo_sai()
    {
        foreach (var mood in new[] { TimeMood.InMeeting, TimeMood.EndingSoon, TimeMood.Overrun })
            Assert.Null(Aviso(At(13, 5), mood));
    }

    /// <summary>
    /// Humor falando de um evento que não está na agenda carregada. Na dúvida o Tempus cala: sem o
    /// evento não há horário exato nem link, e um aviso sem os dois não serve para nada.
    /// </summary>
    [Fact]
    public void Sem_o_evento_na_agenda_nao_ha_o_que_avisar() =>
        Assert.Null(ToastPolicy.Decide(null, Aproximando(), null, At(12, 50), false, Nada));

    [Fact]
    public void O_botao_de_entrar_leva_ao_link_da_reuniao()
    {
        var acoes = Aviso(At(12, 50))!.Actions;

        Assert.Contains(acoes, a => a.Kind == ToastActionKind.Join && a.Argument == "https://meet/x");
    }

    /// <summary>
    /// O link vem do <b>evento</b>, e não de <see cref="TimeStatus.CallUrl"/>, que é <c>null</c> em
    /// <c>Approaching</c> de propósito (D-016). Lá o silêncio protege contra clicar em "Em breve" e
    /// cair numa sala vazia; aqui o toast nomeia a reunião e o botão nomeia a ação.
    /// </summary>
    [Fact]
    public void O_passageiro_tem_link_mesmo_com_o_humor_sem_url()
    {
        Assert.Null(Aproximando().CallUrl);

        Assert.Contains(Aviso(At(12, 50))!.Actions, a => a.Kind == ToastActionKind.Join);
    }

    /// <summary>
    /// Reunião presencial não tem link — mas continua precisando de saída, e sem <b>nenhum</b>
    /// botão o Windows degrada o cenário fixo para um toast comum, sem avisar ninguém.
    /// </summary>
    [Fact]
    public void Reuniao_sem_link_ainda_sai_com_botao()
    {
        var presencial = Aviso(At(12, 59), TimeMood.Imminent, Reuniao(url: null))!;

        Assert.True(presencial.StaysOnScreen);
        Assert.DoesNotContain(presencial.Actions, a => a.Kind == ToastActionKind.Join);
        Assert.NotEmpty(presencial.Actions);
    }

    [Fact]
    public void Reuniao_desligada_nas_configuracoes_nao_interrompe() =>
        Assert.Null(Aviso(At(12, 50), options: new ToastOptions { Meetings = false }));

    /// <summary>Desligar a reunião não pode desligar o vermelho: são pesos diferentes.</summary>
    [Fact]
    public void Desligar_a_reuniao_preserva_o_nivel_3()
    {
        var so3 = new ToastOptions { Meetings = false };

        Assert.NotNull(
            ToastPolicy.Decide(Critical(), Livre, null, At(17, 1), false, Nada, so3));
    }

    [Fact]
    public void Apresentacao_retem_o_aviso_de_reuniao_tambem()
    {
        var retido = ToastPolicy.Decide(
            null, Aproximando(), Reuniao(), At(12, 50), suppressed: true, Nada);

        Assert.Null(retido);
        Assert.NotNull(Aviso(At(12, 52)));
    }

    /// <summary>
    /// Quando as duas coisas cabem no mesmo instante, interrompe a pior: estar invadindo a próxima
    /// call é notícia mais urgente que a seguinte estar por vir.
    /// </summary>
    [Fact]
    public void O_vermelho_ganha_do_aviso_de_reuniao()
    {
        var pedido = ToastPolicy.Decide(
            Critical(), Aproximando(), Reuniao(), At(12, 50), false, Nada)!;

        Assert.Equal(ToastKind.Entry, pedido.Kind);
    }

    // ================================================================ o que não pode degradar

    /// <summary>
    /// <b>A regra do Windows, trancada aqui.</b> Um toast com <c>scenario="reminder"</c> e nenhum
    /// botão não fica na tela: o shell o trata como toast comum e não reclama. Falha silenciosa em
    /// cima de uma decisão de produto — parece que ela foi tomada e ela foi ignorada.
    /// </summary>
    [Fact]
    public void Todo_toast_que_fica_na_tela_tem_pelo_menos_um_botao()
    {
        ToastRequest?[] todos =
        [
            Decide(Critical(), At(17, 1)),
            Decide(Critical(since: At(17)), At(17, 9)),
            Aviso(At(12, 50)),
            Aviso(At(12, 59), TimeMood.Imminent),
            Aviso(At(12, 59), TimeMood.Imminent, Reuniao(url: null)),
        ];

        foreach (var pedido in todos)
        {
            if (pedido is not { StaysOnScreen: true }) continue;

            Assert.True(pedido.Actions.Count > 0, $"{pedido.Kind} fica na tela sem botão");
        }
    }

    /// <summary>
    /// A contraparte da regra 2 na notificação: <b>todo nível 3 é reconhecível</b>. Se o toast
    /// interrompe e não oferece o "eu vi", ele cobra um gesto que só existe em outro lugar.
    /// </summary>
    [Fact]
    public void Todo_toast_de_nivel_3_oferece_o_eu_vi()
    {
        foreach (var pedido in new[] { Decide(Critical(), At(17, 1))!, Decide(Critical(since: At(17)), At(17, 9))! })
        {
            var euVi = pedido.Actions.SingleOrDefault(a => a.Kind == ToastActionKind.Acknowledge);

            Assert.NotNull(euVi);
            Assert.Equal(pedido.Occurrence, euVi!.Argument);
        }
    }

    /// <summary>
    /// O aviso passageiro morre na Central quando a reunião começa: depois disso ele só informaria
    /// um horário que já passou. O fixo não expira por tempo — ele sai por gesto.
    /// </summary>
    [Fact]
    public void O_passageiro_expira_no_inicio_da_reuniao_e_o_fixo_nao()
    {
        Assert.Equal(At(13), Aviso(At(12, 50))!.ExpiresAt);
        Assert.Null(Aviso(At(12, 59), TimeMood.Imminent)!.ExpiresAt);
    }
}
