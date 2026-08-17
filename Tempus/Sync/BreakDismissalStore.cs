using System.IO;

namespace Tempus.Sync;

/// <summary>
/// Guarda o "hoje não quero folga". Um arquivo de uma linha em <c>%APPDATA%\Tempus</c>, contendo a
/// data dispensada em <c>yyyy-MM-dd</c>.
/// <para>
/// Precisa de disco, e não de memória, porque a barra reinicia — em atualização, em restart do
/// explorer, em logoff. Dispensar a pausa e vê-la voltar dez minutos depois ensinaria o usuário a
/// não confiar no gesto.
/// </para>
/// <para>
/// A data <b>é</b> o registro: guardar um booleano exigiria limpá-lo na virada do dia, e um dia em
/// que a limpeza não roda é um dia sem pausa. Comparar datas não tem esse modo de falha — qualquer
/// dia que não seja o gravado tem folga.
/// </para>
/// </summary>
internal sealed class BreakDismissalStore
{
    private readonly string _path;

    public BreakDismissalStore(string dataDirectory) =>
        _path = Path.Combine(dataDirectory, "break-dismissed");

    public bool IsDismissed(DateOnly day)
    {
        try
        {
            return File.Exists(_path)
                && DateOnly.TryParse(File.ReadAllText(_path).Trim(), out var stored)
                && stored == day;
        }
        catch (Exception)
        {
            // Não conseguir ler é razão para ter folga, não para não ter.
            return false;
        }
    }

    public void Dismiss(DateOnly day) => Write(day.ToString("yyyy-MM-dd"));

    /// <summary>Desfazer a dispensa. Grava vazio em vez de apagar: um caminho de escrita só.</summary>
    public void Restore() => Write(string.Empty);

    private void Write(string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, content);
        }
        catch (Exception)
        {
            // Sem disco a dispensa vale só para esta execução. Degradar é melhor que derrubar.
        }
    }
}
