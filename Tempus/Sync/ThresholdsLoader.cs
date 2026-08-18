using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tempus.Domain;

namespace Tempus.Sync;

/// <summary>
/// Lê a seção <c>Time</c> do appsettings. Fica aqui, e não em <see cref="TimeThresholds"/>, para
/// que o domínio continue sem I/O — sinais são funções puras (regra 8).
/// </summary>
internal static class ThresholdsLoader
{
    public static TimeThresholds Load(string path) =>
        Section(path, "Time", TimeThresholds.Default);

    public static WorkDayOptions LoadWorkDay(string path) =>
        Section(path, "WorkDay", WorkDayOptions.Default);

    /// <summary>
    /// Pausas de descanso. O fallback tem <c>Enabled = false</c>: sem seção no appsettings, a
    /// funcionalidade não existe — é opt-in de instalação, não padrão.
    /// </summary>
    public static BreakOptions LoadBreaks(string path) =>
        Section(path, "Breaks", BreakOptions.Default);

    /// <summary>
    /// O que conta como tarefa aberta no fim de jornada (D-007). O padrao AllOpen foi escolhido
    /// sabendo que podia se arrepender; trocar por DueTodayOrOverdue e uma linha, nao um redesenho.
    /// </summary>
    public static DayEndedOptions LoadDayEnded(string path) =>
        Section(path, "DayEnded", DayEndedOptions.Default);

    private static T Section<T>(string path, string name, T fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty(name, out var section)) return fallback;

            return section.Deserialize<T>(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,

                // Enums por nome: "AllOpen" no arquivo e mais legivel que 0, e sobrevive a
                // reordenacao do enum sem mudar de significado.
                Converters = { new JsonStringEnumConverter() },
            }) ?? fallback;
        }
        catch (Exception)
        {
            // Config inválida nunca deve impedir a barra de subir.
            return fallback;
        }
    }
}
