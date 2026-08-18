using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tempus.Domain;

namespace Tempus.Sync;

/// <summary>
/// O que o usuário disse sobre as pausas de <b>hoje</b>: se dispensou o dia e quais já tirou.
/// <para>
/// A data <b>é</b> o registro. Guardar booleanos exigiria limpá-los na virada do dia, e um dia em
/// que a limpeza não roda seria um dia sem pausa. Comparar datas não tem esse modo de falha:
/// qualquer dia diferente do gravado começa limpo.
/// </para>
/// </summary>
internal sealed record BreakDayState
{
    public string? Date { get; init; }

    /// <summary>"Hoje não quero pausa": vale para o dia inteiro.</summary>
    public bool Dismissed { get; init; }

    /// <summary>Períodos que o usuário marcou como já tirados, clicando.</summary>
    public List<BreakPeriod> Taken { get; init; } = [];

    [JsonIgnore]
    public static BreakDayState Empty { get; } = new();

    public bool AppliesTo(DateOnly day) =>
        Date is not null && DateOnly.TryParse(Date, out var stored) && stored == day;
}

/// <summary>
/// Persiste <see cref="BreakDayState"/> em <c>%APPDATA%\Tempus</c>.
/// <para>
/// Precisa de disco, e não de memória, porque a barra reinicia — em atualização, em restart do
/// explorer, em logoff. Marcar a pausa como tirada e vê-la voltar dez minutos depois ensinaria o
/// usuário a não confiar no gesto.
/// </para>
/// <para>
/// <b>O app nunca infere se a pausa foi tirada</b> (D-006): não há detecção de presença, de
/// microfone nem de janela. Ou o usuário clica, ou o Tempus não sabe — e não sabendo, ele se cala
/// em vez de cobrar.
/// </para>
/// </summary>
internal sealed class BreakDismissalStore
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public BreakDismissalStore(string dataDirectory) =>
        _path = Path.Combine(dataDirectory, "break-state.json");

    /// <summary>O estado de hoje, ou vazio se o arquivo é de outro dia.</summary>
    public BreakDayState Load(DateOnly day)
    {
        try
        {
            if (!File.Exists(_path)) return BreakDayState.Empty;

            var stored = JsonSerializer.Deserialize<BreakDayState>(File.ReadAllText(_path), Format);
            return stored is not null && stored.AppliesTo(day) ? stored : BreakDayState.Empty;
        }
        catch (Exception)
        {
            // Não conseguir ler é razão para ter folga, não para não ter.
            return BreakDayState.Empty;
        }
    }

    public void Save(DateOnly day, BreakDayState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(state with { Date = day.ToString("yyyy-MM-dd") }, Format));
        }
        catch (Exception)
        {
            // Sem disco o gesto vale só para esta execução. Degradar é melhor que derrubar.
        }
    }
}
