using System.IO;
using System.Text.Json;

namespace Tempus.Sync;

/// <summary>
/// As ocorrências que o usuário já reconheceu hoje (<c>SEVERITY.md</c> §7, Q-01).
/// <para>
/// Em disco, e não em memória, porque a barra reinicia — em atualização, em restart do explorer,
/// em logoff. Reconhecer um alarme e vê-lo voltar dois minutos depois ensinaria a ignorar o
/// vermelho, que é o único ativo que o modelo de severidade não pode perder.
/// </para>
/// <para>
/// A data <b>é</b> o registro, pelo mesmo motivo do D-019: um booleano exigiria limpeza na virada
/// do dia, e um dia sem limpeza seria um dia de alarmes calados. Qualquer dia diferente do gravado
/// começa limpo.
/// </para>
/// </summary>
internal sealed class AcknowledgementStore
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private readonly string _path;

    public AcknowledgementStore(string dataDirectory) =>
        _path = Path.Combine(dataDirectory, "acknowledged.json");

    public HashSet<string> Load(DateOnly day)
    {
        try
        {
            if (!File.Exists(_path)) return [];

            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path), Format);

            return stored is not null
                && DateOnly.TryParse(stored.Date, out var when)
                && when == day
                    ? [.. stored.Occurrences]
                    : [];
        }
        catch (Exception)
        {
            // Não conseguir ler é razão para alarmar, não para calar.
            return [];
        }
    }

    public void Save(DateOnly day, IEnumerable<string> occurrences)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(
                new Stored { Date = day.ToString("yyyy-MM-dd"), Occurrences = [.. occurrences] },
                Format));
        }
        catch (Exception)
        {
            // Sem disco o reconhecimento vale só para esta execução. Degradar é melhor que derrubar.
        }
    }

    private sealed record Stored
    {
        public string? Date { get; init; }
        public List<string> Occurrences { get; init; } = [];
    }
}
