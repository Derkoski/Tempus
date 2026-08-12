using System.IO;
using System.Text.Json;

namespace Tempus.Sync;

internal sealed record GoogleOptions
{
    /// <summary>
    /// Pré-seleciona a conta na tela de consent. Sem isto, o re-consent semanal (D-003) obriga a
    /// escolher a conta toda vez — com isto, é só clicar em Permitir.
    /// </summary>
    public string? LoginHint { get; init; }

    /// <summary>
    /// Consulta que define o que o contador de e-mail conta, na mesma sintaxe da busca do Gmail.
    /// <para>
    /// O padrão é o que a maioria entende por "esperando por mim": não lidos, na caixa de entrada,
    /// na aba Principal. Contar o rótulo <c>INBOX</c> inteiro incluiria Promoções, Social e
    /// Atualizações — anos de newsletter não lida — e o número deixaria de significar algo.
    /// </para>
    /// <para>
    /// Padrão deliberadamente sem <c>category:primary</c>: o Gmail só aplica rótulos de categoria
    /// quando as abas estão ligadas, então essa cláusula devolve <b>zero</b> em caixa sem abas —
    /// um modo de falha silencioso, pior que um número grande. Adicione-a se quiser filtrar
    /// Promoções, Social e Atualizações.
    /// </para>
    /// </summary>
    public string MailQuery { get; init; } = "is:unread in:inbox";

    /// <summary>
    /// Pasta de dados do app. Fora do repositório e fora de <c>bin/</c>, para o token e o
    /// client_secret sobreviverem a rebuild e nunca entrarem no git (regra 5).
    /// </summary>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tempus");

    public static string ClientSecretPath => Path.Combine(DataDirectory, "client_secret.json");

    public static string TokenDirectory => Path.Combine(DataDirectory, "tokens");

    public static GoogleOptions Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new GoogleOptions();

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("Google", out var google)) return new GoogleOptions();

            return google.Deserialize<GoogleOptions>(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new GoogleOptions();
        }
        catch (Exception)
        {
            return new GoogleOptions();
        }
    }
}
