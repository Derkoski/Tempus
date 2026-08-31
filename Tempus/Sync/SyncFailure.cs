using Google.Apis.Auth.OAuth2.Responses;

namespace Tempus.Sync;

/// <summary>O usuário recusou o consent, ou o Google devolveu erro na tela de autorização.</summary>
internal sealed class ConsentDeniedException(string message) : Exception(message);

/// <summary>
/// O navegador foi aberto e o callback nunca chegou — aba fechada, ou consent deixado pela metade.
/// </summary>
internal sealed class ConsentAbandonedException() : Exception("Consent não concluído");

/// <summary>
/// Que tipo de falha aconteceu, e portanto qual gesto resolve.
/// <para>
/// Existe porque a versão anterior não fazia essa distinção: <b>toda</b> exceção da conexão virava
/// "Login do Google expirou". Um cabo caído, um proxy, um DNS lento logo depois de a máquina
/// retomar do sono — tudo pedia um consent novo, que não conserta nenhum dos três. Pior: o estado
/// de login inválido <b>fura a tolerância de 10 minutos</b> e apaga a barra na hora, enquanto uma
/// falha de rede deveria ser absorvida em silêncio e resolver-se sozinha no ciclo seguinte.
/// </para>
/// </summary>
internal static class SyncFailure
{
    /// <summary>
    /// Só o que um consent novo de fato resolve. Tudo o mais é transporte, e transporte se trata
    /// com paciência, não com navegador.
    /// </summary>
    public static bool NeedsConsent(Exception ex) => ex
        is TokenResponseException      // invalid_grant: os 7 dias do D-003, ou acesso revogado
        or ConsentDeniedException
        or ConsentAbandonedException;
}
