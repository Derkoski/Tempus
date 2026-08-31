using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using Tempus.Sync;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// Que falha pede consent e que falha pede paciência.
/// <para>
/// A distinção parece cosmética e não é: o estado de login inválido <b>fura a tolerância de 10
/// minutos</b> do §0 e apaga a barra na hora, enquanto o de sync caído preserva o último retrato e
/// deixa o ciclo seguinte se resolver sozinho. Classificar rede como login trocava um contratempo
/// invisível por uma barra cinza pedindo um consent que não conserta cabo.
/// </para>
/// </summary>
public class SyncFailureTests
{
    /// <summary>
    /// <c>invalid_grant</c> é o caminho da semana: o refresh token de 7 dias do modo Testing
    /// venceu (D-003), ou o acesso foi revogado. Aqui o navegador é a resposta certa.
    /// </summary>
    [Fact]
    public void Token_vencido_pede_consent()
    {
        var ex = new TokenResponseException(
            new TokenErrorResponse { Error = "invalid_grant" });

        Assert.True(SyncFailure.NeedsConsent(ex));
    }

    [Fact]
    public void Consent_recusado_pede_consent() =>
        Assert.True(SyncFailure.NeedsConsent(new ConsentDeniedException("access_denied")));

    [Fact]
    public void Consent_abandonado_pede_consent() =>
        Assert.True(SyncFailure.NeedsConsent(new ConsentAbandonedException()));

    /// <summary>
    /// O caso que motivou tudo. Logo depois de a máquina retomar do sono a pilha de rede ainda não
    /// subiu, e era exatamente aí que o app anunciava login expirado.
    /// </summary>
    [Theory]
    [MemberData(nameof(FalhasDeTransporte))]
    public void Falha_de_rede_nao_pede_consent(Exception ex) =>
        Assert.False(SyncFailure.NeedsConsent(ex));

    public static TheoryData<Exception> FalhasDeTransporte() =>
    [
        new HttpRequestException("nome não resolvido"),
        new SocketException(10060),
        new TaskCanceledException("tempo esgotado"),
        new IOException("conexão encerrada pelo host"),
        new GoogleApiException("calendar", "backend error"),
    ];
}
