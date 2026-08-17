using System.Globalization;

namespace Tempus.Domain;

/// <summary>
/// O próximo compromisso <b>além de hoje</b>, para ocupar a barra quando o dia já não tem mais
/// nada.
/// <para>
/// É a única informação da barra que não está em lugar nenhum: às 21h de quarta, "amanhã · Daily
/// 09:00" responde o que você vai encontrar ao ligar a máquina. O slot de tempo cuida do dia
/// corrente e fica em <c>Dia Encerrado</c>; este cuida do que vem depois.
/// </para>
/// </summary>
internal static class Lookahead
{
    /// <summary>
    /// Descreve o primeiro compromisso a partir de amanhã, ou <c>null</c> se não houver nenhum na
    /// janela sincronizada. Função pura (regra 8).
    /// </summary>
    public static string? Describe(IReadOnlyList<AgendaItem> upcoming, DateTimeOffset now)
    {
        var tomorrow = DateOnly.FromDateTime(now.Date).AddDays(1);

        AgendaItem? first = null;
        foreach (var item in upcoming)
        {
            if (item.IsAllDay) continue;
            if (DateOnly.FromDateTime(item.Start.ToLocalTime().Date) < tomorrow) continue;
            if (first is null || item.Start < first.Start) first = item;
        }

        if (first is null) return null;

        var local = first.Start.ToLocalTime();
        var day = DateOnly.FromDateTime(local.Date);

        var when = (day.DayOfNumber() - tomorrow.DayOfNumber()) switch
        {
            0 => "amanhã",
            // Dentro da semana o dia abreviado ("sex") situa melhor que uma data.
            < 6 => Abbreviate(local.DayOfWeek),
            _ => local.ToString("dd/MM", CultureInfo.InvariantCulture),
        };

        return $"{when} · {first.Title} {local:HH:mm}";
    }

    private static string Abbreviate(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "seg",
        DayOfWeek.Tuesday => "ter",
        DayOfWeek.Wednesday => "qua",
        DayOfWeek.Thursday => "qui",
        DayOfWeek.Friday => "sex",
        DayOfWeek.Saturday => "sáb",
        _ => "dom",
    };

    private static int DayOfNumber(this DateOnly date) => date.DayNumber;
}
