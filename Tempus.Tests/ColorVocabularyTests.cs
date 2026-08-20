using Tempus.Domain;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// Invariante I9: a barra nunca exibe duas áreas coloridas na mesma família <b>com a mesma forma</b>.
/// </summary>
public class ColorVocabularyTests
{
    private static readonly TimeMood[] Moods = Enum.GetValues<TimeMood>();
    private static readonly Severity[] Severities = Enum.GetValues<Severity>();

    /// <summary>
    /// O slot preenche em <c>EndingSoon</c>, <c>Imminent</c> e <c>Overrun</c>; o chip preenche
    /// sempre que tem severidade. Espelha <c>TimeStatus.IsFilled</c> e a paleta.
    /// </summary>
    private static bool SlotIsFilled(TimeMood mood) =>
        mood is TimeMood.EndingSoon or TimeMood.Imminent or TimeMood.Overrun;

    // ---------------------------------------------------------------- I9, exaustivo

    /// <summary>
    /// Os 8 × 4 pares possíveis. Sempre que as famílias batem, a colisão tem de ser detectada —
    /// é o que faz o chip perder o preenchimento e as duas áreas deixarem de parecer iguais.
    /// </summary>
    [Fact]
    public void Nenhum_par_sobra_com_mesma_familia_e_mesma_forma()
    {
        foreach (var mood in Moods)
        {
            foreach (var severity in Severities)
            {
                var mesmaFamilia = ColorVocabulary.Of(mood) == ColorVocabulary.Of(severity)
                    && ColorVocabulary.Of(mood) is not (ColorFamily.Neutral or ColorFamily.Gray);

                if (!mesmaFamilia) continue;

                Assert.True(
                    ColorVocabulary.Collide(mood, severity),
                    $"{mood} + {severity} caem na mesma família e não foram separados");
            }
        }
    }

    [Fact]
    public void Familias_diferentes_nunca_colidem()
    {
        foreach (var mood in Moods)
        {
            foreach (var severity in Severities)
            {
                if (ColorVocabulary.Of(mood) == ColorVocabulary.Of(severity)) continue;

                Assert.False(
                    ColorVocabulary.Collide(mood, severity),
                    $"{mood} + {severity} são famílias diferentes e não deveriam colidir");
            }
        }
    }

    /// <summary>
    /// O pior caso é duas pílulas idênticas: slot preenchido e chip preenchido na mesma família.
    /// Toda ocorrência precisa estar coberta.
    /// </summary>
    [Fact]
    public void Todo_caso_de_duas_pilulas_iguais_esta_coberto()
    {
        var perigosos = new List<string>();

        foreach (var mood in Moods.Where(SlotIsFilled))
        {
            foreach (var severity in Severities.Where(s => s != Severity.Calm))
            {
                if (ColorVocabulary.Of(mood) != ColorVocabulary.Of(severity)) continue;
                if (ColorVocabulary.Collide(mood, severity)) continue;

                perigosos.Add($"{mood}+{severity}");
            }
        }

        Assert.Empty(perigosos);
    }

    // ---------------------------------------------------------------- regressões nomeadas

    /// <summary>Os três casos capturados em uso, que motivaram a invariante.</summary>
    [Fact]
    public void Os_casos_encontrados_em_uso_colidem()
    {
        // "Ocupado" azul ao lado de um chip azul — a queixa original.
        Assert.True(ColorVocabulary.Collide(TimeMood.InMeeting, Severity.Info));

        // Duas pílulas âmbar idênticas.
        Assert.True(ColorVocabulary.Collide(TimeMood.EndingSoon, Severity.Attention));

        // Duas pílulas vermelhas idênticas.
        Assert.True(ColorVocabulary.Collide(TimeMood.Overrun, Severity.Critical));
        Assert.True(ColorVocabulary.Collide(TimeMood.Imminent, Severity.Critical));

        // "Em breve" e "Dia Encerrado" usam a mesma tinta âmbar no texto do slot.
        Assert.True(ColorVocabulary.Collide(TimeMood.Approaching, Severity.Attention));
        Assert.True(ColorVocabulary.Collide(TimeMood.OffHours, Severity.Attention));
    }

    [Fact]
    public void Combinacoes_legiveis_continuam_preenchidas()
    {
        // Verde ao lado de âmbar, azul ao lado de vermelho: já se distinguem sozinhos.
        Assert.False(ColorVocabulary.Collide(TimeMood.Free, Severity.Attention));
        Assert.False(ColorVocabulary.Collide(TimeMood.InMeeting, Severity.Critical));
        Assert.False(ColorVocabulary.Collide(TimeMood.EndingSoon, Severity.Critical));
    }

    /// <summary>Sem alarme não há segunda área — nada a separar.</summary>
    [Fact]
    public void Severidade_calma_nunca_colide()
    {
        foreach (var mood in Moods)
            Assert.False(ColorVocabulary.Collide(mood, Severity.Calm));
    }

    /// <summary>
    /// <c>Offline</c> precede e anula a escala (§0): nenhum sinal é avaliado, então não existe
    /// segunda área para competir com o cinza.
    /// </summary>
    [Fact]
    public void Offline_nunca_colide()
    {
        foreach (var severity in Severities)
            Assert.False(ColorVocabulary.Collide(TimeMood.Unknown, severity));
    }
}
