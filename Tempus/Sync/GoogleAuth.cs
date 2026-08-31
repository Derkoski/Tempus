using System.IO;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Gmail.v1;
using Google.Apis.Tasks.v1;

namespace Tempus.Sync;

/// <summary>
/// O fluxo OAuth. Duas entradas distintas de propósito:
/// <list type="bullet">
/// <item><see cref="TryRestoreAsync"/> — silencioso, nunca abre navegador. É o que roda no
/// startup: se o token morreu, a barra vai para <c>Offline</c> e espera o clique.</item>
/// <item><see cref="AuthorizeAsync"/> — abre o navegador. Só a partir de um gesto do usuário.</item>
/// </list>
/// <para>
/// Essa separação existe porque D-003 fixou o modo Testing permanentemente: o refresh token
/// morre a cada 7 dias, então o caminho "token expirado" é rotina semanal, não exceção. Abrir
/// navegador sozinho no startup seria hostil.
/// </para>
/// </summary>
internal sealed class GoogleAuth
{
    private const string UserKey = "user";

    /// <summary>
    /// <c>Tasks</c> (escrita) e não <c>TasksReadonly</c>: concluir e criar tarefa é requisito
    /// central, e trocar de escopo depois custaria um consent extra sem ganho.
    /// <para>
    /// <c>GmailReadonly</c> é escopo <b>restricted</b>. Funciona sem verificação porque o app fica
    /// permanentemente em Testing e a conta é usuária de teste (D-003). O Tempus lê apenas o
    /// contador do rótulo INBOX — nunca conteúdo de mensagem (fora de escopo no SPEC).
    /// </para>
    /// </summary>
    public static readonly string[] Scopes =
    [
        CalendarService.Scope.CalendarReadonly,
        TasksService.Scope.Tasks,
        GmailService.Scope.GmailReadonly,
    ];

    private readonly string _clientSecretPath;
    private readonly string? _loginHint;
    private readonly DpapiDataStore _store;

    public GoogleAuth(string clientSecretPath, string tokenDirectory, string? loginHint)
    {
        _clientSecretPath = clientSecretPath;
        _loginHint = loginHint;
        _store = new DpapiDataStore(tokenDirectory);
    }

    public bool HasClientSecret => File.Exists(_clientSecretPath);

    public string ClientSecretPath => _clientSecretPath;

    /// <summary>
    /// Se já houve um consent antes. Distingue "nunca conectou" de "o token dos 7 dias venceu" —
    /// dizer "expirou" na primeira execução seria mentira, e atrapalharia justamente o diagnóstico
    /// de quando o consent é recusado pelo administrador.
    /// </summary>
    public async Task<bool> HasStoredTokenAsync() =>
        (await _store.GetAsync<TokenResponse>(UserKey))?.RefreshToken is not null;

    /// <summary>
    /// Tenta reusar o token guardado. Retorna <c>null</c> quando é preciso consentir de novo —
    /// que é o esperado toda semana (D-003), não um erro.
    /// </summary>
    public async Task<UserCredential?> TryRestoreAsync(CancellationToken ct)
    {
        if (!HasClientSecret) return null;

        var flow = await CreateFlowAsync(ct);
        var token = await _store.GetAsync<TokenResponse>(UserKey);
        if (token?.RefreshToken is null) return null;

        var credential = new UserCredential(flow, UserKey, token);

        try
        {
            // Um refresh explícito é o único teste honesto: o token pode estar revogado sem que
            // nada no arquivo local indique isso.
            if (!await credential.RefreshTokenAsync(ct)) return null;
        }
        catch (TokenResponseException)
        {
            return null; // invalid_grant — os 7 dias venceram, ou o acesso foi revogado
        }

        return credential;
    }

    /// <summary>
    /// Abre o navegador para consentir. Chamar apenas a partir de um clique do usuário.
    /// <para>
    /// Faz o fluxo à mão em vez de usar <c>AuthorizationCodeInstalledApp.AuthorizeAsync</c>, que
    /// <b>consulta o cache antes de abrir o navegador</b>: achando um refresh token válido, ele
    /// devolve a credencial guardada sem perguntar nada. O critério dele é "tem refresh token?",
    /// não "tem os escopos de que eu preciso?" — então adicionar um escopo novo (o Gmail) seria
    /// silenciosamente ignorado, e o clique de "Reconectar" não faria efeito visível nenhum.
    /// </para>
    /// <para>
    /// Ordem importa: o token guardado só é substituído <b>depois</b> de um consent bem-sucedido.
    /// Apagar antes seria mais simples, mas deslogaria o usuário se ele fechasse o navegador.
    /// </para>
    /// </summary>
    public async Task<UserCredential> AuthorizeAsync(CancellationToken ct)
    {
        var flow = await CreateFlowAsync(ct);
        var receiver = new LocalServerCodeReceiver();

        AuthorizationCodeResponseUrl response;

        try
        {
            response = await receiver.ReceiveCodeAsync(
                flow.CreateAuthorizationCodeRequest(receiver.RedirectUri), ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // O token linkado estourou o prazo: aba fechada, ou consent deixado pela metade.
            // Sem isto a espera não termina nunca, e a barra fica em "Abrindo o navegador…" para
            // sempre — dizendo algo que já deixou de ser verdade, que é o que a regra 10 proíbe.
            throw new ConsentAbandonedException();
        }

        if (string.IsNullOrEmpty(response.Code))
        {
            throw new ConsentDeniedException(
                $"consent recusado ({response.Error}) {response.ErrorDescription}".Trim());
        }

        // Grava no DataStore como efeito colateral.
        var token = await flow.ExchangeCodeForTokenAsync(
            UserKey, response.Code, receiver.RedirectUri, ct);

        return new UserCredential(flow, UserKey, token);
    }

    public Task SignOutAsync() => _store.ClearAsync();

    private async Task<IAuthorizationCodeFlow> CreateFlowAsync(CancellationToken ct)
    {
        await using var stream = File.OpenRead(_clientSecretPath);
        var secrets = (await GoogleClientSecrets.FromStreamAsync(stream, ct)).Secrets;

        return new HintedFlow(
            new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = secrets,
                Scopes = Scopes,
                DataStore = _store,
                // Sem "consent" explícito o Google pode devolver a autorização sem refresh token
                // quando já houve consent antes — e aí o app só duraria uma hora.
                Prompt = "consent",
            },
            _loginHint);
    }

    /// <summary>
    /// Injeta <c>login_hint</c> na URL de consent, para que a conta já venha pré-selecionada.
    /// É o que transforma o re-consent semanal em dois cliques (<c>SEVERITY.md</c> §6).
    /// </summary>
    private sealed class HintedFlow(
        GoogleAuthorizationCodeFlow.Initializer initializer,
        string? loginHint) : GoogleAuthorizationCodeFlow(initializer)
    {
        public override AuthorizationCodeRequestUrl CreateAuthorizationCodeRequest(string redirectUri)
        {
            var request = (GoogleAuthorizationCodeRequestUrl)base.CreateAuthorizationCodeRequest(redirectUri);
            if (!string.IsNullOrWhiteSpace(loginHint)) request.LoginHint = loginHint;

            return request;
        }
    }
}
