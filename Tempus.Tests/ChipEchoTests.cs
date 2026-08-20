using Tempus.Domain;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// Quando o chip só repete o que o slot de tempo já conta (D-033), ele não é desenhado — e
/// <b>só isso</b> muda.
/// </summary>
public class ChipEchoTests
{
    private const string Daily = "evento-daily";
    private const string Review = "evento-review";

    private static bool Repeats(TimeMood mood, string signal, string? slot = Daily, string? chip = Daily) =>
        ChipEcho.Repeats(mood, signal, slot, chip);

    // ---------------------------------------------------------------- a tabela

    [Fact]
    public void Cada_humor_cala_o_sinal_que_ele_ja_conta()
    {
        Assert.True(Repeats(TimeMood.Approaching, SignalNames.MeetingUpcoming));
        Assert.True(Repeats(TimeMood.Imminent, SignalNames.MeetingImminent));
        Assert.True(Repeats(TimeMood.InMeeting, SignalNames.MeetingStarted));
        Assert.True(Repeats(TimeMood.InMeeting, SignalNames.MeetingBackToBack));
        Assert.True(Repeats(TimeMood.EndingSoon, SignalNames.MeetingStarted));
        Assert.True(Repeats(TimeMood.EndingSoon, SignalNames.MeetingBackToBack));
        Assert.True(Repeats(TimeMood.Overrun, SignalNames.MeetingRanIntoNext));
        Assert.True(Repeats(TimeMood.Overrun, SignalNames.MeetingEnded));
    }

    /// <summary>
    /// Os limiares do humor e do sinal <b>não se alinham</b>: o humor vira <c>Imminent</c> a 5 min
    /// e o sinal só vira <c>MeetingImminent</c> a 2. Entre 5 e 2 minutos sobrava
    /// <c>MeetingUpcoming</c> sem par, e a barra mostrava "Começando" ao lado de "Fulano em 3 min"
    /// — o mesmo fato duas vezes. Encontrado numa captura de tela, não pelos testes (D-037).
    /// </summary>
    [Fact]
    public void Os_dois_humores_de_proxima_reuniao_absorvem_os_dois_sinais_de_aproximacao()
    {
        Assert.True(Repeats(TimeMood.Imminent, SignalNames.MeetingUpcoming));
        Assert.True(Repeats(TimeMood.Imminent, SignalNames.MeetingImminent));
        Assert.True(Repeats(TimeMood.Approaching, SignalNames.MeetingUpcoming));
        Assert.True(Repeats(TimeMood.Approaching, SignalNames.MeetingImminent));
    }

    /// <summary>
    /// O caso que a regra "toda categoria Call é eco" teria quebrado: aqui o chip está
    /// <b>perguntando</b> em qual reunião você está, e o clique dele abre o seletor do §8. O slot
    /// não tem como fazer essa pergunta.
    /// </summary>
    [Fact]
    public void A_pergunta_de_reunioes_sobrepostas_nunca_e_calada()
    {
        foreach (var mood in Enum.GetValues<TimeMood>())
            Assert.False(Repeats(mood, SignalNames.MeetingAmbiguous));
    }

    /// <summary>O slot fala de call; ele não sabe dizer nada sobre tarefas nem sobre o dia.</summary>
    [Fact]
    public void O_que_o_slot_nao_sabe_dizer_nunca_e_calado()
    {
        string[] alheios =
            [SignalNames.TaskOverdue, SignalNames.MiddayCheckpoint, SignalNames.DayEnded];

        foreach (var mood in Enum.GetValues<TimeMood>())
            foreach (var signal in alheios)
                Assert.False(Repeats(mood, signal));
    }

    // ---------------------------------------------------------------- mesmo evento

    /// <summary>
    /// Dois compromissos diferentes são dois assuntos, mesmo com o par certo de humor e sinal.
    /// Calar aqui esconderia informação nova.
    /// </summary>
    [Fact]
    public void Evento_diferente_nao_e_repeticao()
    {
        Assert.False(Repeats(TimeMood.InMeeting, SignalNames.MeetingStarted, Daily, Review));
        Assert.False(Repeats(TimeMood.Overrun, SignalNames.MeetingRanIntoNext, Daily, Review));
    }

    /// <summary>
    /// Na dúvida o chip fala. Perder um alarme é caro; repetir uma informação, não — então a
    /// ausência de evento de qualquer um dos lados nunca autoriza a supressão.
    /// </summary>
    [Fact]
    public void Sem_evento_de_algum_dos_lados_o_chip_fala()
    {
        Assert.False(Repeats(TimeMood.InMeeting, SignalNames.MeetingStarted, null, Daily));
        Assert.False(Repeats(TimeMood.InMeeting, SignalNames.MeetingStarted, Daily, null));
        Assert.False(Repeats(TimeMood.InMeeting, SignalNames.MeetingStarted, null, null));
    }

    [Fact]
    public void Sem_sinal_vencedor_nao_ha_o_que_calar() =>
        Assert.False(ChipEcho.Repeats(TimeMood.InMeeting, null, Daily, Daily));

    // ---------------------------------------------------------------- humores que não narram call

    /// <summary>
    /// <c>Livre</c>, <c>Dia Encerrado</c> e <c>Offline</c> não contam história de reunião nenhuma,
    /// então nunca há o que o chip esteja repetindo.
    /// </summary>
    [Fact]
    public void Humores_sem_narrativa_de_call_nunca_calam_o_chip()
    {
        foreach (var mood in new[] { TimeMood.Free, TimeMood.OffHours, TimeMood.Unknown })
        {
            Assert.False(Repeats(mood, SignalNames.MeetingStarted));
            Assert.False(Repeats(mood, SignalNames.MeetingRanIntoNext));
        }
    }

    // ---------------------------------------------------------------- é só pintura

    /// <summary>
    /// A garantia que sustenta a decisão: o sinal calado <b>continua</b> vencendo a arbitragem, e
    /// por isso continua alimentando o toast (§5) e aceitando o "eu vi" (§10). Se a supressão
    /// mexesse na arbitragem, o vermelho das 17:00 poderia sumir sem ninguém ver.
    /// </summary>
    [Fact]
    public void Calar_o_chip_nao_tira_o_sinal_da_arbitragem()
    {
        var estourou = new Signal
        {
            Name = SignalNames.MeetingRanIntoNext,
            Severity = Severity.Critical,
            Reason = "Daily acabou — Review já começou",
            Category = SignalCategory.Call,
            Occurrence = "MeetingRanIntoNext|evento-daily",
            EventId = Daily,
            Since = DateTimeOffset.Now.AddMinutes(-2),
        };

        Assert.True(Repeats(TimeMood.Overrun, estourou.Name, Daily, estourou.EventId));

        // A arbitragem não conhece o eco: quem decide o vencedor é só a severidade e a categoria.
        var vencedor = Arbiter.Winner([estourou]);

        Assert.Same(estourou, vencedor);
        Assert.Equal(Severity.Critical, vencedor!.Severity);
        Assert.False(vencedor.SelfClearing);
    }
}
