using Tempus.Domain;
using Xunit;

namespace Tempus.Tests;

/// <summary>Fronteiras do dia e feriados (D-007, D-008).</summary>
public class WorkDayTests
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

    [Theory]
    [InlineData(8, "Dia Encerrado")]  // antes de começar
    [InlineData(12, "Almoço")]
    [InlineData(18, "Dia Encerrado")]
    public void Fora_do_expediente_tem_rotulo_proprio(int hour, string esperado) =>
        Assert.Equal(esperado, WorkDayResolver.OffHoursLabel(At(hour), Work));

    [Theory]
    [InlineData(12, 30, "Almoço")]
    [InlineData(13, 0, null)]
    public void Volta_do_almoco_encerra_a_janela(int hour, int minute, string? esperado) =>
        Assert.Equal(esperado, WorkDayResolver.OffHoursLabel(At(hour, minute), Work));

    [Fact]
    public void Dentro_do_expediente_nao_tem_rotulo() =>
        Assert.Null(WorkDayResolver.OffHoursLabel(At(10), Work));

    [Fact]
    public void Fim_de_semana_e_folga()
    {
        var domingo = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.FromHours(-3));

        Assert.Equal("Folga", WorkDayResolver.OffHoursLabel(domingo, Work));
    }

    /// <summary>
    /// Páscoa por Meeus/Jones/Butcher. Os anos abaixo são conhecidos e servem de âncora — um erro
    /// no algoritmo desloca Carnaval, Sexta-feira Santa e Corpus Christi de uma vez.
    /// </summary>
    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    public void Pascoa_bate_com_as_datas_conhecidas(int ano, int mes, int dia) =>
        Assert.Equal(new DateOnly(ano, mes, dia), BrazilianHolidays.Easter(ano));

    [Theory]
    [InlineData(2026, 1, 1)]    // Confraternização
    [InlineData(2026, 4, 3)]    // Sexta-feira Santa (Páscoa 05/04 menos 2)
    [InlineData(2026, 9, 7)]    // Independência
    [InlineData(2026, 11, 20)]  // Consciência Negra, nacional desde 2024
    [InlineData(2026, 12, 19)]  // Emancipação do Paraná
    [InlineData(2026, 12, 14)]  // Emancipação de Pato Branco
    public void Feriados_sao_reconhecidos(int ano, int mes, int dia) =>
        Assert.True(BrazilianHolidays.IsHoliday(new DateOnly(ano, mes, dia)));

    [Fact]
    public void Dia_util_comum_nao_e_feriado() =>
        Assert.False(BrazilianHolidays.IsHoliday(new DateOnly(2026, 8, 19)));

    [Fact]
    public void Feriado_nao_e_dia_de_trabalho() =>
        Assert.False(WorkDayResolver.IsWorkingDay(new DateOnly(2026, 9, 7), Work));

    [Fact]
    public void Emenda_manual_tira_o_dia_do_expediente()
    {
        var comEmenda = Work with { ExtraHolidays = ["2026-08-19"] };

        Assert.False(WorkDayResolver.IsWorkingDay(new DateOnly(2026, 8, 19), comEmenda));
    }

    // ---------------------------------------------------------------- contagem regressiva

    [Fact]
    public void Contagem_aparece_dentro_da_janela_e_some_fora()
    {
        Assert.Null(WorkDayResolver.Resolve(At(10), Work));          // longe do meio-dia
        Assert.NotNull(WorkDayResolver.Resolve(At(11, 30), Work));   // 30 min para 12:00
    }

    [Fact]
    public void Contagem_aponta_para_a_fronteira_certa()
    {
        var manha = WorkDayResolver.Resolve(At(11, 30), Work);
        Assert.Equal("12:00", manha!.At);
        Assert.Equal(30, manha.Minutes);

        var tarde = WorkDayResolver.Resolve(At(16, 40), Work);
        Assert.Equal("17:00", tarde!.At);
        Assert.Equal(20, tarde.Minutes);
    }

    [Fact]
    public void Depois_do_expediente_nao_ha_contagem() =>
        Assert.Null(WorkDayResolver.Resolve(At(17, 30), Work));
}
