using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tempus.Domain;

namespace Tempus.Sync;

/// <summary>
/// As escritas que ainda não subiram, em disco.
/// <para>
/// Em disco porque o caso que dói é justamente o que atravessa o fechamento do app: você digita
/// uma tarefa com a rede caída, fecha o notebook, e o título só existe aqui. Concluir e excluir se
/// refazem olhando a lista; um título digitado, não.
/// </para>
/// <para>
/// Não é escopado por dia, ao contrário do <see cref="AcknowledgementStore"/>: escrita pendente
/// não é coisa de dia, e uma que virasse o relógio sem subir continuaria devendo.
/// </para>
/// </summary>
internal sealed class PendingWriteStore
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    /// <param name="fileName">
    /// O modo demo usa arquivo próprio: uma escrita falsa não pode entrar na fila que vai subir
    /// para a conta de verdade.
    /// </param>
    public PendingWriteStore(string dataDirectory, string fileName = "pending-writes.json") =>
        _path = Path.Combine(dataDirectory, fileName);

    public List<PendingWrite> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];

            var stored = JsonSerializer.Deserialize<List<PendingWrite>>(
                File.ReadAllText(_path), Format) ?? [];

            // Quem estava pendente ganha chance limpa: o app caiu, mas a rede pode ter voltado.
            // Quem já tinha desistido continua desistido — a decisão de repetir é do usuário.
            return [.. stored.Select(w =>
                w.State == WriteState.Pending ? WritePolicy.Revive(w) : w)];
        }
        catch (Exception)
        {
            // Arquivo corrompido não pode impedir a barra de subir. Perde-se a fila, não o app.
            return [];
        }
    }

    public void Save(IEnumerable<PendingWrite> writes)
    {
        try
        {
            var pending = writes.ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Fila vazia apaga o arquivo em vez de gravar "[]": o estado normal do app é não dever
            // nada, e a ausência do arquivo torna isso óbvio para quem for olhar a pasta.
            if (pending.Count == 0)
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(pending, Format));
        }
        catch (Exception)
        {
            // Sem disco a fila vale só para esta execução. Degradar é melhor que derrubar.
        }
    }
}
