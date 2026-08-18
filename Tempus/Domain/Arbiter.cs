namespace Tempus.Domain;

/// <summary>
/// A arbitragem do <c>SEVERITY.md</c> §4: entre os sinais ativos, um só chega à barra.
/// <para>
/// <c>severidadeEfetiva = max(sinais não suprimidos)</c>, com desempate por categoria. É o que
/// sustenta a invariante I1 — o chip exibe <b>uma</b> severidade por vez, nunca dois alarmes
/// competindo pelo mesmo olhar.
/// </para>
/// </summary>
internal static class Arbiter
{
    /// <summary>
    /// O sinal vencedor, ou <c>null</c> quando não há nenhum ativo.
    /// <para>
    /// Desempate por categoria (§4): ciclo de call vence fronteira do dia, que vence tarefas. A
    /// razão é temporal — reunião perdida não volta, tarefa vencida continua lá amanhã. Empate
    /// dentro da mesma categoria fica com o primeiro, que é estável porque a ordem de geração é
    /// determinística.
    /// </para>
    /// </summary>
    public static Signal? Winner(IReadOnlyList<Signal> active) =>
        active.Count == 0
            ? null
            : active
                .OrderByDescending(s => s.Severity)
                .ThenByDescending(s => s.Category)
                .First();
}

/// <summary>
/// A histerese das invariantes I4 e I5: <b>subir é imediato, descer espera</b>.
/// <para>
/// Existe para a barra não tremer em torno de uma fronteira. Sem ela, um sinal que oscila entre
/// dois níveis a cada rodada de sync — ou a cada segundo, perto de um limiar — piscaria sem que
/// nada de fato tenha mudado, e o usuário aprenderia a ignorar a barra.
/// </para>
/// <para>
/// <b>Guarda estado de propósito</b>, ao contrário do resto do domínio: histerese é sobre
/// <i>quanto tempo faz</i>, e isso não sai de (dados, agora). O estado é mínimo — o nível exibido
/// e desde quando — e a classe é determinística: mesma sequência de entradas, mesma saída.
/// </para>
/// </summary>
internal sealed class SeverityGate
{
    private readonly TimeSpan _minimumHold;

    private Severity _shown = Severity.Calm;
    private DateTimeOffset _since;
    private Signal? _winner;

    /// <param name="minimumHold">Tempo mínimo num nível antes de aceitar rebaixamento (I4: 20s).</param>
    public SeverityGate(TimeSpan? minimumHold = null) =>
        _minimumHold = minimumHold ?? TimeSpan.FromSeconds(20);

    public Severity Shown => _shown;

    /// <summary>O sinal que está sendo exibido, que pode ser mais antigo que o candidato atual.</summary>
    public Signal? Winner => _winner;

    /// <summary>
    /// Passa o vencedor da arbitragem pela histerese e devolve o que a barra deve exibir.
    /// </summary>
    public Signal? Apply(Signal? candidate, DateTimeOffset now)
    {
        var level = candidate?.Severity ?? Severity.Calm;

        // I5: subida é imediata, sempre. Atrasar um alarme para não tremer seria trocar o problema
        // certo pelo errado — tremer incomoda, chegar tarde custa.
        if (level > _shown)
        {
            _shown = level;
            _since = now;
            _winner = candidate;
            return _winner;
        }

        if (level == _shown)
        {
            // Mesmo nível, sinal possivelmente diferente: troca o motivo sem reiniciar o relógio.
            // Reiniciar aqui deixaria a barra presa num nível enquanto sinais se revezassem nele.
            _winner = candidate;
            return _winner;
        }

        // I4: descer espera o mínimo. Enquanto não vence, a barra continua mostrando o anterior.
        if (now - _since < _minimumHold) return _winner;

        _shown = level;
        _since = now;
        _winner = candidate;
        return _winner;
    }

    /// <summary>
    /// Reconhecimento: desce na hora, sem esperar (I4 diz explicitamente que a histerese não se
    /// aplica a ele). Quem clicou "eu vi" merece resposta imediata, ou o gesto parece quebrado.
    /// </summary>
    public void Reset(DateTimeOffset now)
    {
        _shown = Severity.Calm;
        _since = now;
        _winner = null;
    }
}
