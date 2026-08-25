using Tempus.Domain;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// "Você tem horário livre quinta?" (D-043). É onde mora o risco de responder <b>errado com
/// confiança</b> — prometer um horário que não existe, ou esconder um que existe.
/// </summary>
public class DayAvailabilityTests
{
    // 2026-08-25 é uma terça-feira; 29 e 30 são sábado e domingo.
    private static readonly DateOnly Terca = new(2026, 8, 25);
    private static readonly DateOnly Sabado = new(2026, 8, 29);
    private static readonly TimeSpan Fuso = TimeSpan.FromHours(-3);

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, Fuso);

    /// <summary>Madrugada do dia: antes de tudo, para o "agora" não recortar nada por acidente.</summary>
    private static DateTimeOffset Cedo(DateOnly day) => At(day, 1);

    private static AgendaItem Event(DateOnly day, int fromHour, int toHour, string title = "call") =>
        new()
        {
            Id = $"{title}-{fromHour}",
            Title = title,
            Start = At(day, fromHour),
            End = At(day, toHour),
        };

    private static DaySummary Day(IReadOnlyList<AgendaItem> agenda, DateOnly? day = null,
        DateTimeOffset? now = null) =>
        DayAvailability.Describe(
            agenda, day ?? Terca, WorkDayOptions.Default, now ?? Cedo(day ?? Terca));

    private static string[] Labels(DaySummary summary) => [.. summary.Free.Select(f => f.Label)];

    // ---------------------------------------------------------------- o dia vazio

    /// <summary>
    /// Expediente é 08:00–17:00 com almoço das 12:00 às 13:00. Um dia útil sem nada marcado são
    /// <b>duas</b> janelas, não uma — e oferecer 08:00–17:00 corrido convidaria a marcar reunião
    /// no almoço.
    /// </summary>
    [Fact]
    public void Dia_util_vazio_da_o_expediente_menos_o_almoco()
    {
        var dia = Day([]);

        Assert.Equal(["08:00–12:00", "13:00–17:00"], Labels(dia));
        Assert.Equal(0, dia.Meetings);
        Assert.True(dia.IsWorkingDay);
        Assert.Null(dia.Note);
    }

    // ---------------------------------------------------------------- subtração

    [Fact]
    public void Reuniao_recorta_a_janela_em_volta_dela()
    {
        var dia = Day([Event(Terca, 9, 10)]);

        Assert.Equal(["08:00–09:00", "10:00–12:00", "13:00–17:00"], Labels(dia));
        Assert.Equal(1, dia.Meetings);
    }

    /// <summary>
    /// Duas calls simultâneas — rotina nesta agenda (§8). Sem fundir os blocos ocupados antes de
    /// subtrair, a segunda abriria entre elas um buraco que não existe.
    /// </summary>
    [Fact]
    public void Reunioes_sobrepostas_nao_inventam_buraco()
    {
        var dia = Day([Event(Terca, 9, 11, "a"), Event(Terca, 10, 12, "b")]);

        Assert.Equal(["08:00–09:00", "13:00–17:00"], Labels(dia));
        Assert.Equal(2, dia.Meetings);
    }

    [Fact]
    public void Reunioes_coladas_nao_deixam_janela_entre_si()
    {
        var dia = Day([Event(Terca, 9, 10, "a"), Event(Terca, 10, 11, "b")]);

        Assert.Equal(["08:00–09:00", "11:00–12:00", "13:00–17:00"], Labels(dia));
    }

    /// <summary>
    /// Vão de dez minutos não é horário disponível. Listá-lo transformaria a resposta em ruído —
    /// ninguém marca reunião ali, e a lista existe para dizer onde cabe alguma coisa.
    /// </summary>
    [Fact]
    public void Vao_curto_demais_nao_conta_como_livre()
    {
        IReadOnlyList<AgendaItem> agenda =
        [
            new AgendaItem
            {
                Id = "a", Title = "a", Start = At(Terca, 8), End = At(Terca, 9),
            },
            new AgendaItem
            {
                Id = "b", Title = "b", Start = At(Terca, 9, 10), End = At(Terca, 12),
            },
        ];

        // Os 10 min entre 09:00 e 09:10 somem; a tarde inteira permanece.
        Assert.Equal(["13:00–17:00"], Labels(Day(agenda)));
    }

    [Fact]
    public void Agenda_cheia_diz_que_esta_cheia()
    {
        var dia = Day([Event(Terca, 8, 12, "manhã"), Event(Terca, 13, 17, "tarde")]);

        Assert.Empty(dia.Free);
        Assert.Equal("Agenda cheia", dia.Note);
    }

    // ---------------------------------------------------------------- o presente

    /// <summary>
    /// Hoje começa <b>agora</b>. Um resumo que às 16h diga "livre 09:00–12:00" está respondendo
    /// sobre um dia que já não existe.
    /// </summary>
    [Fact]
    public void Hoje_comeca_agora_e_nao_no_inicio_do_expediente()
    {
        var dia = Day([], now: At(Terca, 14, 30));

        Assert.Equal(["14:30–17:00"], Labels(dia));
    }

    [Fact]
    public void Depois_do_expediente_nao_sobra_janela_hoje()
    {
        var dia = Day([], now: At(Terca, 18));

        Assert.Empty(dia.Free);
        Assert.Equal("Expediente encerrado", dia.Note);
    }

    /// <summary>O corte pelo presente vale só para hoje — amanhã começa no início do expediente.</summary>
    [Fact]
    public void Dia_futuro_nao_e_recortado_pela_hora_atual()
    {
        var amanha = Terca.AddDays(1);
        var dia = Day([], amanha, now: At(Terca, 16));

        Assert.Equal(["08:00–12:00", "13:00–17:00"], Labels(dia));
    }

    // ---------------------------------------------------------------- dia não útil

    /// <summary>
    /// Sábado dizendo "livre o dia todo" seria uma resposta errada com cara de certa — e é
    /// justamente a que faria o usuário prometer um horário que ele não quer dar.
    /// </summary>
    [Fact]
    public void Fim_de_semana_nao_oferece_horario()
    {
        var dia = Day([], Sabado);

        Assert.Empty(dia.Free);
        Assert.False(dia.IsWorkingDay);
        Assert.Equal("Fim de semana", dia.Note);
    }

    [Fact]
    public void Feriado_nao_oferece_horario_e_diz_o_motivo()
    {
        // 7 de setembro de 2026 cai numa segunda-feira: dia útil pelo calendário, feriado por lei.
        var independencia = new DateOnly(2026, 9, 7);

        Assert.Equal(DayOfWeek.Monday, independencia.DayOfWeek);
        Assert.False(WorkDayResolver.IsWorkingDay(independencia, WorkDayOptions.Default));

        var dia = Day([], independencia);

        Assert.Empty(dia.Free);
        Assert.Equal("Feriado", dia.Note);
    }

    /// <summary>Compromisso marcado num sábado continua sendo contado — ele existe.</summary>
    [Fact]
    public void Compromisso_em_dia_nao_util_ainda_conta()
    {
        Assert.Equal(1, Day([Event(Sabado, 10, 11)], Sabado).Meetings);
    }

    // ---------------------------------------------------------------- alheios ao dia

    [Fact]
    public void Dia_inteiro_nao_ocupa_horario_nem_conta()
    {
        IReadOnlyList<AgendaItem> agenda =
        [
            new AgendaItem
            {
                Id = "f", Title = "Férias do time",
                Start = At(Terca, 0), End = At(Terca.AddDays(1), 0),
                IsAllDay = true,
            },
        ];

        var dia = Day(agenda);

        Assert.Equal(["08:00–12:00", "13:00–17:00"], Labels(dia));
        Assert.Equal(0, dia.Meetings);
    }

    [Fact]
    public void Compromisso_de_outro_dia_nao_entra()
    {
        var dia = Day([Event(Terca.AddDays(1), 9, 10)]);

        Assert.Equal(["08:00–12:00", "13:00–17:00"], Labels(dia));
        Assert.Equal(0, dia.Meetings);
    }

    // ---------------------------------------------------------------- a lista

    [Fact]
    public void Summarize_devolve_um_resumo_por_dia_em_ordem()
    {
        var dias = DayAvailability.Summarize(
            [], Terca, 15, WorkDayOptions.Default, Cedo(Terca));

        Assert.Equal(15, dias.Count);
        Assert.Equal(Terca, dias[0].Day);
        Assert.Equal(Terca.AddDays(14), dias[^1].Day);
        Assert.Equal(dias.Select(d => d.Day).OrderBy(d => d), dias.Select(d => d.Day));
    }
}
