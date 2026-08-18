using Tempus.Domain;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// Pausas de descanso (D-019, D-023).
/// <para>
/// Metade destes testes são <b>regressões de defeitos que chegaram ao usuário</b> em 2026-08-18.
/// Cada um deles era lógica pura, sem UI e sem rede — exatamente o que este projeto existe para
/// pegar em segundos, e que na ausência dele só apareceu depois de incomodar.
/// </para>
/// </summary>
public class BreakPlannerTests
{
    private static readonly WorkDayOptions Work = new()
    {
        StartHour = 9,
        MiddayHour = 12,
        LunchEndHour = 13,
        EndHour = 17,
    };

    private static readonly BreakOptions Enabled = new() { Enabled = true, DurationMinutes = 15 };

    /// <summary>Uma quarta-feira comum, para não esbarrar em fim de semana nem feriado.</summary>
    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 8, 19, hour, minute, 0, TimeSpan.FromHours(-3));

    private static AgendaItem Meeting(string id, DateTimeOffset start, DateTimeOffset end) =>
        new() { Id = id, Title = id, Start = start, End = end };

    // ---------------------------------------------------------------- básico

    [Fact]
    public void Desabilitada_por_padrao_nao_planeja_nada()
    {
        var slots = BreakPlanner.Plan([], At(10), Work, BreakOptions.Default);

        Assert.Empty(slots);
    }

    [Fact]
    public void Agenda_vazia_poe_a_pausa_no_meio_de_cada_periodo()
    {
        var slots = BreakPlanner.Plan([], At(10), Work, Enabled);

        Assert.Equal(2, slots.Count);

        // Manhã 09:00–12:00: o meio da janela de 15 min é 10:22:30; a grade de 5 min dá 10:20.
        Assert.Equal(At(10, 20), slots[0].Start);
        Assert.Equal(BreakPeriod.Morning, slots[0].Period);

        // Tarde 13:00–17:00: meio em 14:52:30 → 14:50.
        Assert.Equal(At(14, 50), slots[1].Start);
        Assert.Equal(BreakPeriod.Afternoon, slots[1].Period);
    }

    [Fact]
    public void Nao_inventa_pausa_em_periodo_sem_janela_livre()
    {
        var lotado = new[] { Meeting("tarde", At(13), At(17)) };

        var slots = BreakPlanner.Plan(lotado, At(14), Work, Enabled);

        Assert.DoesNotContain(slots, s => s.Period == BreakPeriod.Afternoon);
    }

    // ---------------------------------------------------------------- regressões

    /// <summary>
    /// <b>Regressão.</b> A pausa reagendava para frente quando vencia, e perseguia o usuário o dia
    /// inteiro — <i>"sempre tô com pausa pra fazer"</i>. Ela é do <b>período</b>, não do instante:
    /// a mesma agenda tem que produzir o mesmo horário, seja consultada de manhã ou de tarde.
    /// </summary>
    [Fact]
    public void Nao_persegue_o_usuario_quando_a_pausa_vence()
    {
        var cedo = BreakPlanner.Plan([], At(9, 5), Work, Enabled);
        var depois = BreakPlanner.Plan([], At(16, 30), Work, Enabled);

        Assert.Equal(
            cedo.Select(s => s.Start),
            depois.Select(s => s.Start));
    }

    /// <summary>
    /// <b>Regressão.</b> Pedido explícito do usuário: uma call em cima da folga tem que empurrá-la
    /// para depois da call. Aconteceu de verdade — um treinamento de 14:00–15:00 moveu a pausa da
    /// tarde para 15:00.
    /// </summary>
    [Fact]
    public void Call_em_cima_da_folga_empurra_para_depois_dela()
    {
        var agenda = new[] { Meeting("treinamento", At(14), At(15)) };

        var slots = BreakPlanner.Plan(agenda, At(13, 30), Work, Enabled);
        var tarde = slots.Single(s => s.Period == BreakPeriod.Afternoon);

        Assert.Equal(At(15), tarde.Start);
    }

    /// <summary>
    /// <b>Regressão.</b> Evento de dia inteiro ocupava as 8 horas e apagava as duas pausas de
    /// qualquer dia com férias ou aniversário no calendário. Ele não impede ninguém de levantar
    /// da cadeira.
    /// </summary>
    [Fact]
    public void Evento_de_dia_inteiro_nao_bloqueia_pausa()
    {
        var ferias = new[]
        {
            new AgendaItem
            {
                Id = "ferias",
                Title = "Férias do time",
                Start = At(0),
                End = At(23, 59),
                IsAllDay = true,
            },
        };

        var slots = BreakPlanner.Plan(ferias, At(10), Work, Enabled);

        Assert.Equal(2, slots.Count);
    }

    /// <summary>
    /// <b>Regressão.</b> O piso do adiamento era <c>agora + 30</c>. Adiar às 15:58 uma pausa
    /// marcada para 17:50 a puxava para 16:30 — antecipar, não adiar. Aqui o piso já vem calculado
    /// pela camada de cima; o planejador só precisa respeitá-lo.
    /// </summary>
    [Fact]
    public void Adiamento_respeita_o_piso_e_pega_a_primeira_janela_depois_dele()
    {
        var piso = new Dictionary<BreakPeriod, DateTimeOffset> { [BreakPeriod.Afternoon] = At(15, 42) };

        var slots = BreakPlanner.Plan([], At(15), Work, Enabled, piso);
        var tarde = slots.Single(s => s.Period == BreakPeriod.Afternoon);

        // Primeira posição da grade de 5 min a partir do piso.
        Assert.Equal(At(15, 45), tarde.Start);
    }

    /// <summary>
    /// Depois de adiada, o alvo deixa de ser o meio do período: quem adiou quer a folga assim que
    /// der, não no horário ideal. Sem isto o planejador voltaria a puxá-la para o meio.
    /// </summary>
    [Fact]
    public void Adiada_busca_o_quanto_antes_e_nao_o_meio_do_periodo()
    {
        var piso = new Dictionary<BreakPeriod, DateTimeOffset> { [BreakPeriod.Afternoon] = At(13, 30) };

        var slots = BreakPlanner.Plan([], At(13), Work, Enabled, piso);
        var tarde = slots.Single(s => s.Period == BreakPeriod.Afternoon);

        Assert.Equal(At(13, 30), tarde.Start);
        Assert.NotEqual(At(14, 50), tarde.Start);
    }

    [Fact]
    public void Fim_de_semana_nao_tem_pausa()
    {
        var sabado = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.FromHours(-3));

        Assert.Empty(BreakPlanner.Plan([], sabado, Work, Enabled));
    }
}
