using Tempus.Domain;
using Tempus.Shell;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// As invariantes I1–I8 do <c>SEVERITY.md</c> §1.
/// <para>
/// <b>Nem todas são testáveis hoje.</b> I4, I5 e I8 dependem de máquina de estados, histerese e
/// escalada, que a Fase 3 ainda não construiu. Elas aparecem aqui como testes marcados
/// <c>Skip</c>, com o motivo — um teste ausente some da vista, um teste pulado cobra.
/// </para>
/// </summary>
public class InvariantTests
{
    private static readonly WorkDayOptions Work = new()
    {
        StartHour = 9,
        MiddayHour = 12,
        LunchEndHour = 13,
        EndHour = 17,
    };

    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 8, 19, hour, minute, 0, TimeSpan.FromHours(-3));

    // ---------------------------------------------------------------- I2

    /// <summary>
    /// I2: toda severidade ≥ 1 tem motivo legível. Nunca colorir sem explicar.
    /// <para>
    /// Sem <c>[Theory]</c> tipada porque <see cref="Severity"/> é <c>internal</c> e um método
    /// público de teste não pode receber parâmetro menos acessível que ele.
    /// </para>
    /// </summary>
    [Fact]
    public void I2_severidade_colorida_sempre_tem_motivo()
    {
        foreach (var nivel in new[] { Severity.Info, Severity.Attention, Severity.Critical })
        {
            var comMotivo = new ShellState { Severity = nivel, Reason = "Motivo qualquer" };
            var semMotivo = new ShellState { Severity = nivel, Reason = "" };

            Assert.True(Respeita(comMotivo));
            Assert.False(Respeita(semMotivo));
        }

        // Calm é o único nível que pode existir sem motivo.
        Assert.True(Respeita(new ShellState { Severity = Severity.Calm, Reason = "" }));

        static bool Respeita(ShellState s) =>
            s.Severity == Severity.Calm || !string.IsNullOrWhiteSpace(s.Reason);
    }

    // ---------------------------------------------------------------- I3

    /// <summary>
    /// I3: todo sinal de nível 3 é reconhecível por clique, e reconhecer sempre o remove
    /// imediatamente. Nenhum nível 3 pode ser inescapável.
    /// <para>
    /// Esta é a invariante do Q-01: ela foi violada em produção por três semanas porque
    /// <c>CanAcknowledge</c> nunca era ligado no caminho de dados reais.
    /// </para>
    /// </summary>
    [Fact]
    public void I3_todo_alarme_oferece_reconhecimento_e_ele_resolve_na_hora()
    {
        var weekly = new AgendaItem
        {
            Id = "weekly",
            Title = "Weekly",
            Start = At(14),
            End = At(15),
        };

        var alarme = TimeStatusResolver.Resolve(
            [weekly], At(15, 3), TimeThresholds.Default, Work);

        // Alarmando ⇒ existe ocorrência a reconhecer.
        Assert.True(alarme.IsFilled);
        Assert.NotNull(alarme.Occurrence);

        // E o estado que a UI monta tem que oferecer o gesto.
        var comAlarme = new ShellState { Time = alarme, CanAcknowledge = alarme.Occurrence is not null };
        Assert.True(comAlarme.CanAcknowledge);

        // Reconhecido ⇒ sai imediatamente, sem esperar nada.
        var depois = TimeStatusResolver.Resolve(
            [weekly], At(15, 3), TimeThresholds.Default, Work,
            new HashSet<string> { alarme.Occurrence! });

        Assert.False(depois.IsFilled);
        Assert.Null(depois.Occurrence);
    }

    /// <summary>Espelho do I3: estado calmo não inventa gesto que não tem o que fazer.</summary>
    [Fact]
    public void I3_estado_calmo_nao_oferece_reconhecimento()
    {
        var calmo = TimeStatusResolver.Resolve([], At(10), TimeThresholds.Default, Work);

        Assert.False(calmo.IsFilled);
        Assert.Null(calmo.Occurrence);
    }

    // ---------------------------------------------------------------- I6 e I7

    /// <summary>
    /// I6: contadores pertencem ao nível <c>Calm</c> mesmo com a barra vermelha. A cor pertence ao
    /// estado, não aos números — aqui, que o valor não é apagado nem alterado pela severidade.
    /// </summary>
    [Fact]
    public void I6_contadores_sobrevivem_a_severidade()
    {
        var vermelha = new ShellState
        {
            Severity = Severity.Critical,
            Reason = "Estourou",
            OpenTasks = 7,
            UnreadMail = 3,
        };

        Assert.Equal(7, vermelha.OpenTasks);
        Assert.Equal(3, vermelha.UnreadMail);
    }

    /// <summary>
    /// I7: em <c>Offline</c> nenhum contador exibe número e nenhum sinal é avaliado. Mostrar o
    /// último valor conhecido seria mentir com confiança (regra 10).
    /// </summary>
    [Fact]
    public void I7_offline_nao_mostra_numero_nem_avalia_sinal()
    {
        var offline = new ShellState
        {
            IsOffline = true,
            Reason = "Login do Google expirou",
            OpenTasks = null,
            UnreadMail = null,
        };

        Assert.Null(offline.OpenTasks);
        Assert.Null(offline.UnreadMail);

        // TimeStatus.Unknown é o que a barra usa quando não sabe de nada.
        Assert.Equal(TimeMood.Unknown, TimeStatus.Unknown.Mood);
        Assert.Null(TimeStatus.Unknown.Occurrence);
        Assert.False(TimeStatus.Unknown.IsFilled);
    }

    // ---------------------------------------------------------------- ainda sem máquina de estados

    [Fact(Skip = "I1 depende da arbitragem (SEVERITY §4), que a Fase 3 ainda não construiu.")]
    public void I1_nunca_dois_alarmes_competindo() { }

    [Fact(Skip = "I4 depende da histerese de 20s na descida, ainda não implementada.")]
    public void I4_histerese_de_20s_antes_de_rebaixar() { }

    [Fact(Skip = "I5 depende da histerese; subida imediata só faz sentido com ela existindo.")]
    public void I5_subida_de_nivel_e_imediata() { }

    /// <summary>
    /// I8: a escalada nunca ocorre antes de 5 min no nível 3, e nunca com período menor que 1s.
    /// </summary>
    [Fact]
    public void I8_escalada_nunca_antes_de_5_min_nem_com_periodo_menor_que_1s()
    {
        var weekly = new AgendaItem
        {
            Id = "weekly",
            Title = "Weekly",
            Start = At(14),
            End = At(15),
        };

        TimeStatus Em(int minutosDepoisDoFim) => TimeStatusResolver.Resolve(
            [weekly], At(15).AddMinutes(minutosDepoisDoFim), TimeThresholds.Default, Work);

        // Vermelho sólido na primeira janela: alarma, mas não pisca.
        Assert.True(Em(1).IsFilled);
        Assert.False(Em(1).IsEscalated);
        Assert.False(Em(4).IsEscalated);

        // A partir de 5 min, escala.
        Assert.True(Em(5).IsEscalated);
        Assert.True(Em(8).IsEscalated);

        // Afrouxar por configuração é permitido; antecipar não.
        var apressado = TimeThresholds.Default with { EscalationMinutes = 1 };
        var cedo = TimeStatusResolver.Resolve([weekly], At(15, 2), apressado, Work);
        Assert.False(cedo.IsEscalated);

        // Período do piscar ≥ 1s.
        Assert.True(FloatingBarWindow.BlinkPeriod >= TimeSpan.FromSeconds(1));
    }

    /// <summary>Reconhecer encerra a escalada junto com o alarme — não existe piscar órfão.</summary>
    [Fact]
    public void I8_reconhecer_encerra_a_escalada()
    {
        var weekly = new AgendaItem
        {
            Id = "weekly",
            Title = "Weekly",
            Start = At(14),
            End = At(15),
        };

        var escalado = TimeStatusResolver.Resolve(
            [weekly], At(15, 7), TimeThresholds.Default, Work);
        Assert.True(escalado.IsEscalated);

        var depois = TimeStatusResolver.Resolve(
            [weekly], At(15, 7), TimeThresholds.Default, Work,
            new HashSet<string> { escalado.Occurrence! });

        Assert.False(depois.IsEscalated);
    }
}
