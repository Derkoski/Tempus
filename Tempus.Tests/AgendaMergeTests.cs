using Tempus.Domain;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// A fusão dos calendários (D-034). O que ela precisa acertar é uma linha fina: apagar a mesma
/// reunião vinda duas vezes, sem apagar duas reuniões diferentes no mesmo horário — que é
/// ambiguidade de verdade e obrigação do §8 perguntar.
/// </summary>
public class AgendaMergeTests
{
    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 8, 20, hour, minute, 0, TimeSpan.FromHours(-3));

    private static AgendaItem Item(
        string id, string title, int hour, int endHour, Conference? call = null) => new()
    {
        Id = id,
        Title = title,
        Start = At(hour),
        End = At(endHour),
        Conference = call,
    };

    // ---------------------------------------------------------------- o caso que motivou

    /// <summary>
    /// A mesma call chegando pelo convite do Google e pelo calendário importado do Teams. Os ids
    /// diferem — cada calendário emite o seu —, então a comparação não pode ser por id.
    /// </summary>
    [Fact]
    public void A_mesma_reuniao_vinda_de_dois_calendarios_vira_uma()
    {
        var merged = AgendaMerge.Dedupe([
            Item("google-abc", "Backlog Pearson", 13, 14),
            Item("teams-xyz", "Backlog Pearson", 13, 14),
        ]);

        Assert.Single(merged);
    }

    /// <summary>
    /// Vence o primeiro, e quem chama põe o principal na frente: é dele que vêm o link da call e o
    /// RSVP. Ficar com a cópia assinada perderia o clique de entrar.
    /// </summary>
    [Fact]
    public void Vence_a_copia_que_chega_primeiro_com_o_link()
    {
        var call = new Conference { Url = "https://meet.google.com/abc", Provider = ConferenceProvider.Meet };

        var merged = AgendaMerge.Dedupe([
            Item("google-abc", "Backlog Pearson", 13, 14, call),
            Item("teams-xyz", "Backlog Pearson", 13, 14),
        ]);

        Assert.Equal("google-abc", merged.Single().Id);
        Assert.NotNull(merged.Single().Conference);
    }

    // ---------------------------------------------------------------- o que NÃO pode sumir

    /// <summary>
    /// Duas reuniões diferentes às 13:00 são ambiguidade real. Fundi-las esconderia um conflito de
    /// agenda — o oposto do que o produto existe para fazer.
    /// </summary>
    [Fact]
    public void Reunioes_diferentes_no_mesmo_horario_continuam_duas()
    {
        var merged = AgendaMerge.Dedupe([
            Item("a", "Backlog Pearson", 13, 14),
            Item("b", "1:1 com o gestor", 13, 14),
        ]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Mesmo_titulo_em_horarios_diferentes_continua_dois()
    {
        var merged = AgendaMerge.Dedupe([
            Item("a", "Daily", 9, 10),
            Item("b", "Daily", 13, 14),
        ]);

        Assert.Equal(2, merged.Count);
    }

    /// <summary>Mesma hora de início, fim diferente: reuniões distintas.</summary>
    [Fact]
    public void Fim_diferente_nao_e_duplicata()
    {
        var merged = AgendaMerge.Dedupe([
            Item("a", "Backlog Pearson", 13, 14),
            Item("b", "Backlog Pearson", 13, 15),
        ]);

        Assert.Equal(2, merged.Count);
    }

    // ---------------------------------------------------------------- normalização

    /// <summary>
    /// Duas importações do mesmo convite costumam diferir num espaço a mais ou numa maiúscula.
    /// Tratá-las como eventos distintos devolveria o falso positivo do §8 que a fusão evita.
    /// </summary>
    [Theory]
    [InlineData("Backlog Pearson", "backlog pearson")]
    [InlineData("Backlog Pearson", "Backlog  Pearson")]
    [InlineData("Backlog Pearson", "  Backlog Pearson  ")]
    public void Caixa_e_espaco_nao_distinguem_compromisso(string a, string b)
    {
        var merged = AgendaMerge.Dedupe([Item("x", a, 13, 14), Item("y", b, 13, 14)]);

        Assert.Single(merged);
    }

    // ---------------------------------------------------------------- básico

    [Fact]
    public void A_ordem_de_chegada_e_preservada()
    {
        var merged = AgendaMerge.Dedupe([
            Item("a", "Daily", 9, 10),
            Item("b", "Backlog Pearson", 13, 14),
            Item("c", "Retro", 16, 17),
        ]);

        Assert.Equal(["Daily", "Backlog Pearson", "Retro"], merged.Select(i => i.Title));
    }

    [Fact]
    public void Lista_vazia_nao_quebra() => Assert.Empty(AgendaMerge.Dedupe([]));
}
