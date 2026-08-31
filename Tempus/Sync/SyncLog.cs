using System.IO;
using System.Text;

namespace Tempus.Sync;

/// <summary>
/// Registro das transições de saúde do sync, em texto, no diretório de dados.
/// <para>
/// Existe por uma falha de diagnóstico concreta: a barra ficou cinza várias vezes num dia e não
/// sobrou <b>nada</b> para dizer por quê. O único vestígio era a data do arquivo de token, e dela
/// só se deduz que o app parou de renovar — não o que aconteceu. Sem trilha, a próxima ocorrência
/// também vira adivinhação.
/// </para>
/// <para>
/// Só <b>transições</b> entram. Um retrato idêntico ao anterior a cada 15 s encheria o arquivo de
/// ruído e esconderia justamente o que se procura.
/// </para>
/// <para>
/// Nada aqui pode derrubar o app: uma falha ao escrever o log é engolida de propósito. É a única
/// exceção honesta à regra 13 — ela vale para escrita que o usuário pediu, e ninguém pediu isto.
/// </para>
/// </summary>
internal sealed class SyncLog(string path)
{
    /// <summary>
    /// Acima disto o arquivo é cortado pela metade, mantendo o fim. Um dia ruim gera dezenas de
    /// linhas, não milhares — o teto existe para o caso patológico, não para o normal.
    /// </summary>
    private const long MaxBytes = 256 * 1024;

    private readonly object _gate = new();

    public static SyncLog InDataDirectory() =>
        new(Path.Combine(GoogleOptions.DataDirectory, "sync-log.txt"));

    public void Record(SyncHealth health, string? message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  {health,-13} {message}".TrimEnd();

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Trim();
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Diagnóstico que atrapalha o app é pior que diagnóstico nenhum.
        }
    }

    /// <summary>
    /// Corta pela metade quando passa do teto. Mantém o <b>fim</b>: o que interessa é sempre o que
    /// acabou de acontecer.
    /// </summary>
    private void Trim()
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= MaxBytes) return;

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        File.WriteAllLines(path, lines[(lines.Length / 2)..], Encoding.UTF8);
    }
}
