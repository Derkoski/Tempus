using Tempus.Domain;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// "Já saí desta reunião" (D-041): a call acabou antes da hora marcada, e a partir do gesto ela
/// para de alimentar a barra.
/// </summary>
public class MeetingLeftTests
{
    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 8, 21, hour, minute, 0, TimeSpan.FromHours(-3));

    private static AgendaItem Item(string id, string title, int from, int to) => new()
    {
        Id = id,
        Title = title,
        Start = At(from),
        End = At(to),
    };

    private static readonly AgendaItem Daily = Item("daily", "Daily KA", 14, 15);
    private static readonly AgendaItem Review = Item("review", "Review", 15, 16);

    private static readonly IReadOnlyList<AgendaItem> Dia = [Daily, Review];

    private static HashSet<string> Encerrou(params AgendaItem[] items) =>
        [.. items.Select(MeetingLeft.OccurrenceFor)];

    private static IReadOnlyList<Signal> Sinais(
        IReadOnlyList<AgendaItem> agenda, DateTimeOffset now, IReadOnlySet<string>? left = null) =>
        Signals.Evaluate(
            MeetingLeft.Apply(agenda, left),
            [],
            now,
            WorkDayOptions.Default,
            SignalThresholds.Default,
            left ?? new HashSet<string>());

    private static TimeStatus Humor(
        IReadOnlyList<AgendaItem> agenda, DateTimeOffset now, IReadOnlySet<string>? left = null) =>
        TimeStatusResolver.Resolve(
            MeetingLeft.Apply(agenda, left),
            now,
            TimeThresholds.Default,
            WorkDayOptions.Default,
            left ?? new HashSet<string>());

    // ---------------------------------------------------------------- o motivo de existir

    /// <summary>
    /// <b>O teste que justifica a mudança.</b> Sem o gesto, sair da Daily às 14:38 não impedia
    /// nada: às 15:00 a Review começa, a Daily "passa do horário", e a barra acende nível 3 — que
    /// escala, pisca e cobra clique — por uma reunião que o usuário deixou vinte minutos antes.
    /// <para>
    /// Vermelho à toa é modelo errado, não tolerância do usuário (regra 1).
    /// </para>
    /// </summary>
    [Fact]
    public void Encerrar_mata_o_vermelho_falso_de_estouro()
    {
        // Sem o gesto: o alarme dispara às 15:05.
        Assert.Contains(
            Sinais(Dia, At(15, 5)),
            s => s.Name == SignalNames.MeetingRanIntoNext);

        // Com "já saí" às 14:38: não há estouro nenhum.
        Assert.DoesNotContain(
            Sinais(Dia, At(15, 5), Encerrou(Daily)),
            s => s.Name == SignalNames.MeetingRanIntoNext);
    }

    /// <summary>
    /// Encerrar remove, e não encurta o fim para "agora" — encurtar faria nascer um
    /// <c>MeetingEnded</c> âmbar. Responder a um "já terminei" explícito com um alerta é a
    /// resposta errada; a resposta certa é silêncio.
    /// </summary>
    [Fact]
    public void O_gesto_e_respondido_com_silencio_e_nao_com_ambar()
    {
        var depois = Sinais(Dia, At(14, 39), Encerrou(Daily));

        Assert.DoesNotContain(depois, s => s.Name == SignalNames.MeetingEnded);
        Assert.DoesNotContain(depois, s => s.EventId == Daily.Id);
    }

    [Fact]
    public void A_contagem_para_e_o_humor_passa_ao_que_vem_depois()
    {
        Assert.Equal(TimeMood.InMeeting, Humor(Dia, At(14, 38)).Mood);

        // 14:38 com a Daily encerrada: a Review é às 15:00, dentro dos 15 min de Approaching.
        var depois = Humor(Dia, At(14, 50), Encerrou(Daily));

        Assert.Equal(TimeMood.Approaching, depois.Mood);
        Assert.Equal(Review.Id, depois.EventId);
    }

    // ---------------------------------------------------------------- alcance do gesto

    [Fact]
    public void So_a_reuniao_encerrada_sai_do_dia()
    {
        var restante = MeetingLeft.Apply(Dia, Encerrou(Daily));

        Assert.Equal([Review], restante);
    }

    [Fact]
    public void Sem_nada_encerrado_a_agenda_passa_intacta()
    {
        Assert.Same(Dia, MeetingLeft.Apply(Dia, null));
        Assert.Same(Dia, MeetingLeft.Apply(Dia, new HashSet<string>()));
    }

    [Fact]
    public void Reabrir_devolve_humor_e_sinais_por_completo()
    {
        var reaberto = Humor(Dia, At(14, 38), new HashSet<string>());

        Assert.Equal(TimeMood.InMeeting, reaberto.Mood);
        Assert.Equal(Daily.Id, reaberto.EventId);
        Assert.Contains(Sinais(Dia, At(14, 38)), s => s.EventId == Daily.Id);
    }

    /// <summary>
    /// §7: a identidade leva início e fim, então prorrogar a reunião no calendário produz
    /// ocorrência nova e ela <b>volta a contar</b>. É o mesmo comportamento que o §7 já define
    /// para o reconhecimento, e aqui também está certo — prorrogar é fato novo.
    /// </summary>
    [Fact]
    public void Reuniao_prorrogada_volta_a_contar()
    {
        var encerrada = Encerrou(Daily);
        var prorrogada = Daily with { End = At(16) };

        Assert.Empty(MeetingLeft.Apply([Daily], encerrada));
        Assert.Equal([prorrogada], MeetingLeft.Apply([prorrogada], encerrada));
    }

    // ---------------------------------------------------------------- o que o menu oferece

    [Fact]
    public void O_encerravel_e_a_reuniao_que_o_humor_nomeia()
    {
        var agora = At(14, 38);
        var corrente = MeetingLeft.Leavable(Dia, Humor(Dia, agora), agora);

        Assert.Equal(Daily, corrente);
    }

    /// <summary>
    /// <c>Estourou</c> só existe quando <b>nada</b> está em curso — reunião correndo sempre ganha
    /// do estouro no resolvedor. Nesse estado o humor nomeia a reunião que já passou do horário,
    /// então não há o que encerrar, e o gesto certo é o "reconhecer alerta" que já existia: ali o
    /// problema não é a contagem, é o alarme.
    /// </summary>
    [Fact]
    public void Em_estouro_nao_ha_encerravel_e_o_gesto_certo_e_reconhecer()
    {
        // Com folga depois da Daily, às 15:05 não há reunião correndo.
        IReadOnlyList<AgendaItem> comFolga = [Daily, Item("tarde", "Retro", 16, 17)];

        var agora = At(15, 5);
        var humor = Humor(comFolga, agora);

        Assert.Equal(TimeMood.Overrun, humor.Mood);
        Assert.Equal(Daily.Id, humor.EventId);
        Assert.Null(MeetingLeft.Leavable(comFolga, humor, agora));
    }

    /// <summary>
    /// O caso que quase virou bug, e o motivo de <c>Leavable</c> perguntar ao humor em vez de
    /// pegar "a primeira reunião em curso".
    /// <para>
    /// Às 15:05, coladas, a barra mostra <b>duas coisas sobre reuniões diferentes</b> (§0.5): o
    /// humor diz <c>Ocupado</c> pela Review, e o chip acende vermelho pela Daily que estourou.
    /// O gesto tem de mirar a Review — a reunião em que o usuário está —, nunca a Daily, que já
    /// acabou e onde encerrar não significaria nada.
    /// </para>
    /// </summary>
    [Fact]
    public void Com_reunioes_coladas_o_gesto_mira_a_que_esta_correndo()
    {
        var agora = At(15, 5);
        var humor = Humor(Dia, agora);

        Assert.Equal(TimeMood.InMeeting, humor.Mood);
        Assert.Contains(Sinais(Dia, agora), s => s.Name == SignalNames.MeetingRanIntoNext);

        Assert.Equal(Review, MeetingLeft.Leavable(Dia, humor, agora));
    }

    [Fact]
    public void Sem_reuniao_em_curso_nao_ha_o_que_encerrar()
    {
        var agora = At(14, 50);
        var so = new[] { Review };

        Assert.Null(MeetingLeft.Leavable(so, Humor(so, agora), agora));
    }

    [Fact]
    public void Da_para_reabrir_enquanto_a_reuniao_ainda_estaria_em_curso()
    {
        Assert.Equal(Daily, MeetingLeft.Reopenable(Dia, Encerrou(Daily), At(14, 50)));
    }

    /// <summary>
    /// Passado o horário dela, reabrir não teria efeito nenhum — a entrada some do menu sozinha,
    /// sem ninguém precisar limpá-la.
    /// </summary>
    [Fact]
    public void Depois_do_horario_nao_ha_mais_o_que_reabrir()
    {
        Assert.Null(MeetingLeft.Reopenable(Dia, Encerrou(Daily), At(15, 30)));
        Assert.Null(MeetingLeft.Reopenable(Dia, null, At(14, 50)));
    }

    // ---------------------------------------------------------------- convivência

    /// <summary>
    /// A ocorrência de "já saí" divide o conjunto persistido com as de "eu vi". Elas não podem se
    /// confundir: encerrar uma reunião não pode calar o alarme de outra coisa, nem vice-versa.
    /// </summary>
    [Fact]
    public void A_ocorrencia_de_ja_sai_nao_colide_com_a_de_eu_vi()
    {
        var sai = MeetingLeft.OccurrenceFor(Daily);

        Assert.StartsWith("MeetingLeft|", sai);
        Assert.NotEqual(Signal.OccurrenceFor(SignalNames.MeetingStarted, Daily), sai);
        Assert.NotEqual(TimeStatusResolver.OccurrenceOf(TimeMood.InMeeting, Daily), sai);
    }

    /// <summary>
    /// O painel do dia não é filtrado: a reunião aconteceu e continua na lista. O gesto para de
    /// cobrar atenção, não reescreve o dia — e é lá que o usuário confere o que já passou.
    /// </summary>
    [Fact]
    public void A_agenda_original_nao_e_modificada()
    {
        var original = Dia;

        MeetingLeft.Apply(original, Encerrou(Daily));

        Assert.Equal(2, original.Count);
        Assert.Contains(Daily, original);
    }
}
