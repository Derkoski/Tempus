namespace Tempus.Domain;

internal enum WriteFailureKind
{
    /// <summary>Rede, timeout, quota, 5xx. Tentar de novo tem chance real de funcionar.</summary>
    Transient,

    /// <summary>A requisição nunca vai dar certo do jeito que está. Insistir só gasta quota.</summary>
    Permanent,
}

/// <summary>
/// Quando repetir uma escrita e quando desistir. Puro, como o resto do domínio (regra 8) — a mesma
/// divisão do D-028: aqui se decide, e o <c>WriteQueue</c> executa.
/// <para>
/// A classificação recebe o <b>código HTTP</b>, não a exceção do SDK do Google. Se recebesse a
/// exceção, o domínio passaria a depender do cliente HTTP e deixaria de ser testável sem ele.
/// </para>
/// </summary>
internal static class WritePolicy
{
    /// <summary>
    /// Três tentativas automáticas. Depois disso o problema provavelmente não é passageiro, e
    /// continuar tentando em silêncio é pior que dizer "não salvou" e esperar o usuário.
    /// </summary>
    public const int MaxAttempts = 3;

    /// <param name="httpStatus"><c>null</c> quando nem chegou a haver resposta — rede caída.</param>
    public static WriteFailureKind Classify(int? httpStatus) => httpStatus switch
    {
        // 400 é requisição malformada e 404/410 é alvo que não existe mais: nenhum melhora com o
        // tempo. Uma conclusão que dá 404 quer dizer que a tarefa sumiu do outro lado — e nesse
        // caso o servidor já está do jeito que o usuário queria.
        400 or 404 or 410 => WriteFailureKind.Permanent,

        // 403 fica DE FORA da lista de propósito: o Google usa 403 tanto para permissão negada
        // quanto para 'rateLimitExceeded'. Tratá-lo como permanente descartaria escrita legítima
        // durante uma rajada. Permissão negada apenas gasta três tentativas antes de virar "não
        // salvou", que é um final honesto.
        _ => WriteFailureKind.Transient,
    };

    /// <summary>Espera antes da próxima tentativa: 2 s, 8 s, 30 s.</summary>
    public static TimeSpan Backoff(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(8),
        _ => TimeSpan.FromSeconds(30),
    };

    /// <summary>A transição de estado depois de uma tentativa que não deu certo.</summary>
    public static PendingWrite AfterFailure(
        PendingWrite write, int? httpStatus, string? message, DateTimeOffset now)
    {
        var attempts = write.Attempts + 1;
        var kind = Classify(httpStatus);
        var exhausted = kind == WriteFailureKind.Permanent || attempts >= MaxAttempts;

        return write with
        {
            Attempts = attempts,
            State = exhausted ? WriteState.Failed : WriteState.Pending,
            NextAttemptAt = exhausted ? null : now + Backoff(attempts),
            Failure = Describe(kind, httpStatus, message),
        };
    }

    /// <summary>Volta a tentar por ordem do usuário: zera o histórico e vale para agora.</summary>
    public static PendingWrite Revive(PendingWrite write) => write with
    {
        Attempts = 0,
        State = WriteState.Pending,
        NextAttemptAt = null,
        Failure = null,
    };

    public static bool IsDue(PendingWrite write, DateTimeOffset now) =>
        write.State == WriteState.Pending && (write.NextAttemptAt is not { } at || now >= at);

    private static string Describe(WriteFailureKind kind, int? httpStatus, string? message) =>
        (kind, httpStatus) switch
        {
            (WriteFailureKind.Permanent, 404 or 410) => "A tarefa não existe mais no Google",
            (WriteFailureKind.Permanent, _) => "O Google recusou a alteração",
            (_, null) => "Sem conexão com o Google",
            _ => message is { Length: > 0 } ? message : $"O Google respondeu {httpStatus}",
        };
}
