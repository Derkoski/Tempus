using Tempus.Domain;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// Humor temporal (<c>SEVERITY.md</c> §1.5) e reconhecimento de ocorrência (§7 e §10).
/// </summary>
public class TimeStatusResolverTests
{
    private static readonly WorkDayOptions Work = new()
    {
        StartHour = 9,
        MiddayHour = 12,
        LunchEndHour = 13,
        EndHour = 17,
    };

    private static readonly TimeThresholds Limits = TimeThresholds.Default;

    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 8, 19, hour, minute, 0, TimeSpan.FromHours(-3));

    private static AgendaItem Meeting(
        string id, DateTimeOffset start, DateTimeOffset end, string? url = null) => new()
        {
            Id = id,
            Title = id,
            Start = start,
            End = end,
            Conference = url is null ? null : Conference.FromUrl(url),
        };

    private static TimeStatus Resolve(
        IReadOnlyList<AgendaItem> agenda, DateTimeOffset now, IReadOnlySet<string>? seen = null) =>
        TimeStatusResolver.Resolve(agenda, now, Limits, Work, seen);

    // ---------------------------------------------------------------- humores

    [Fact]
    public void Sem_compromisso_no_expediente_fica_livre()
    {
        var status = Resolve([], At(10));

        Assert.Equal(TimeMood.Free, status.Mood);
        Assert.Equal("Livre", status.Label);
        Assert.Null(status.Summary);
    }

    /// <summary>
    /// O humor recebido por nome, e não como <see cref="TimeMood"/>: o enum é <c>internal</c> e um
    /// método público de teste não pode receber parâmetro menos acessível que ele.
    /// </summary>
    [Theory]
    [InlineData(60, "Free", "Livre")]
    [InlineData(14, "Approaching", "Em breve")]
    [InlineData(4, "Imminent", "Começando")]
    public void Aproximacao_escala_conforme_os_limiares(int faltam, string humor, string rotulo)
    {
        var inicio = At(15);
        var agenda = new[] { Meeting("daily", inicio, At(16)) };

        var status = Resolve(agenda, inicio.AddMinutes(-faltam));

        Assert.Equal(humor, status.Mood.ToString());
        Assert.Equal(rotulo, status.Label);
    }

    [Fact]
    public void Em_reuniao_fica_ocupado_e_avisa_o_proximo_com_hora_de_relogio()
    {
        var agenda = new[]
        {
            Meeting("refino", At(14), At(15)),
            Meeting("review", At(16), At(17)),
        };

        var status = Resolve(agenda, At(14, 15));

        Assert.Equal(TimeMood.InMeeting, status.Mood);
        Assert.Equal("Ocupado", status.Label);
        Assert.Contains("16:00", status.Summary);
    }

    [Fact]
    public void Estouro_tem_prioridade_sobre_a_proxima_reuniao()
    {
        var agenda = new[]
        {
            Meeting("weekly", At(14), At(15)),
            Meeting("review", At(16), At(17)),
        };

        var status = Resolve(agenda, At(15, 3));

        Assert.Equal(TimeMood.Overrun, status.Mood);
        Assert.Equal("Estourou", status.Label);
    }

    /// <summary>
    /// D-013: encerrar avisa mesmo sem nada em seguida. O custo de estourar é das outras pessoas.
    /// </summary>
    [Fact]
    public void Encerrando_dispara_mesmo_sem_nada_depois()
    {
        var agenda = new[] { Meeting("refino", At(14), At(15)) };

        var status = Resolve(agenda, At(14, 57));

        Assert.Equal(TimeMood.EndingSoon, status.Mood);
    }

    /// <summary>
    /// <b>Regressão.</b> Clicar em "Livre" entrava numa call que só começaria horas depois — cair
    /// numa sala vazia sem perceber. A URL só existe a partir de "Começando".
    /// </summary>
    [Fact]
    public void Call_so_fica_clicavel_a_partir_de_comecando()
    {
        var inicio = At(15);
        var agenda = new[] { Meeting("daily", inicio, At(16), "https://meet.google.com/abc-defg-hij") };

        Assert.Null(Resolve(agenda, inicio.AddMinutes(-90)).CallUrl);   // Livre
        Assert.Null(Resolve(agenda, inicio.AddMinutes(-14)).CallUrl);   // Em breve
        Assert.NotNull(Resolve(agenda, inicio.AddMinutes(-4)).CallUrl); // Começando
    }

    /// <summary>
    /// <b>Regressão.</b> A barra mostrava dois compromissos lado a lado — o de hoje no slot de
    /// estado e o de amanhã no lookahead. Quem nomeia evento marca a bandeira e o lookahead cede.
    /// </summary>
    [Fact]
    public void Estado_que_nomeia_evento_marca_a_bandeira_para_o_lookahead_ceder()
    {
        var agenda = new[] { Meeting("daily", At(15), At(16)) };

        Assert.True(Resolve(agenda, At(14)).NamesAnEvent);
        Assert.False(Resolve([], At(10)).NamesAnEvent);
    }

    // ---------------------------------------------------------------- reconhecimento (§10)

    [Fact]
    public void Reconhecer_estouro_devolve_o_estado_calmo_imediatamente()
    {
        var weekly = Meeting("weekly", At(14), At(15));
        var agenda = new[] { weekly };
        var agora = At(15, 3);

        var antes = Resolve(agenda, agora);
        Assert.Equal(TimeMood.Overrun, antes.Mood);
        Assert.NotNull(antes.Occurrence);

        var depois = Resolve(agenda, agora, new HashSet<string> { antes.Occurrence! });

        Assert.NotEqual(TimeMood.Overrun, depois.Mood);
        Assert.Null(depois.Occurrence);
    }

    /// <summary>
    /// O ponto que fez o sinal entrar na identidade da ocorrência (§10): reconhecer "está
    /// acabando" não pode perdoar de antemão um estouro que ainda não aconteceu.
    /// </summary>
    [Fact]
    public void Reconhecer_encerrando_nao_cala_o_estouro_da_mesma_reuniao()
    {
        var refino = Meeting("refino", At(14), At(15));
        var agenda = new[] { refino };

        var encerrando = Resolve(agenda, At(14, 57));
        Assert.Equal(TimeMood.EndingSoon, encerrando.Mood);

        var seen = new HashSet<string> { encerrando.Occurrence! };

        Assert.Equal(TimeMood.InMeeting, Resolve(agenda, At(14, 57), seen).Mood);
        Assert.Equal(TimeMood.Overrun, Resolve(agenda, At(15, 3), seen).Mood);
    }

    /// <summary>
    /// §7: prorrogar a reunião muda o fim, muda a identidade, e o sinal volta a disparar — o
    /// reconhecimento anterior se referia a outra coisa.
    /// </summary>
    [Fact]
    public void Prorrogar_a_reuniao_faz_o_sinal_voltar_a_disparar()
    {
        var original = Meeting("weekly", At(14), At(15));
        var agora = At(15, 3);

        var reconhecida = Resolve([original], agora).Occurrence!;
        var prorrogada = Meeting("weekly", At(14), At(15, 30));

        var status = Resolve([prorrogada], At(15, 33), new HashSet<string> { reconhecida });

        Assert.Equal(TimeMood.Overrun, status.Mood);
    }

    [Fact]
    public void Reconhecer_uma_reuniao_nao_cala_outra()
    {
        var daily = Meeting("daily", At(14), At(15));
        var review = Meeting("review", At(16), At(17));

        var doDaily = Resolve([daily, review], At(15, 3)).Occurrence!;
        var status = Resolve([review], At(17, 3), new HashSet<string> { doDaily });

        Assert.Equal(TimeMood.Overrun, status.Mood);
    }
}
