using Tempus.Domain;

namespace Tempus.Sync;

/// <summary>O que aconteceu com uma tentativa. Sem tipo de exceção: o domínio não conhece HTTP.</summary>
internal readonly record struct WriteOutcome(bool Success, int? HttpStatus, string? Message)
{
    public static readonly WriteOutcome Ok = new(true, null, null);

    public static WriteOutcome Failed(int? status, string? message) => new(false, status, message);
}

/// <summary>
/// A fila de escritas: FIFO, uma por vez, com repetição automática.
/// <para>
/// A chamada ao Google entra como <b>delegate</b>, e é isso que faz a fila ser testável sem rede —
/// e que faz o modo demo exercitar exatamente este código, e não uma imitação dele. A regra do
/// D-024 (verificar escrita em demo, nunca na conta real) só vale alguma coisa se os dois caminhos
/// forem o mesmo caminho.
/// </para>
/// <para>
/// Decidir <i>quando</i> repetir é do <see cref="WritePolicy"/>. Aqui só se executa e se guarda.
/// </para>
/// </summary>
internal sealed class WriteQueue : IDisposable
{
    public delegate Task<WriteOutcome> Executor(PendingWrite write, CancellationToken ct);

    private readonly Executor _execute;
    private readonly PendingWriteStore _store;
    private readonly List<PendingWrite> _writes;
    private readonly object _gate = new();

    /// <summary>Acorda a bomba quando chega escrita nova, em vez de acordar de tempos em tempos.</summary>
    private readonly SemaphoreSlim _signal = new(0);

    private CancellationTokenSource? _cts;

    public WriteQueue(Executor execute, PendingWriteStore store)
    {
        _execute = execute;
        _store = store;
        _writes = store.Load();
    }

    /// <summary>A fila mudou: a tela precisa se redesenhar.</summary>
    public event EventHandler? Changed;

    /// <summary>Uma escrita subiu e não sobrou nenhuma vencida — hora de reler as tarefas.</summary>
    public event EventHandler? Drained;

    public IReadOnlyList<PendingWrite> Pending
    {
        get { lock (_gate) return [.. _writes]; }
    }

    public bool HasFailures
    {
        get { lock (_gate) return _writes.Any(w => w.State == WriteState.Failed); }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = PumpAsync(_cts.Token);

        // A fila carregada do disco pode já ter trabalho: uma criação que não subiu ontem.
        if (Pending.Count > 0) _signal.Release();
    }

    public void Enqueue(PendingWrite write)
    {
        lock (_gate) _writes.Add(write);

        Announce();
        _signal.Release();
    }

    /// <summary>"Tentar de novo", por ordem do usuário.</summary>
    public void Retry(string id)
    {
        lock (_gate)
        {
            var index = _writes.FindIndex(w => w.Id == id);
            if (index < 0) return;

            _writes[index] = WritePolicy.Revive(_writes[index]);
        }

        Announce();
        _signal.Release();
    }

    /// <summary>"Deixa pra lá": some da fila sem tocar no Google.</summary>
    public void Discard(string id)
    {
        lock (_gate) _writes.RemoveAll(w => w.Id == id);

        Announce();
    }

    /// <summary>
    /// Confronta a fila com o retrato do servidor e descarta o que já aconteceu.
    /// <para>
    /// É o que impede duplicata quando a resposta se perdeu mas a escrita tinha dado certo, e é
    /// onde "Google ganha empate" acontece: confirmada, a intenção deixa de existir.
    /// </para>
    /// </summary>
    public void Confirm(IReadOnlyList<TaskItem> server)
    {
        int removed;
        lock (_gate) removed = _writes.RemoveAll(w => TaskProjection.IsConfirmed(w, server));

        if (removed == 0) return;

        Announce();
    }

    /// <summary>
    /// Executa a próxima escrita vencida, se houver. Devolve <c>false</c> quando não havia nada a
    /// fazer. Exposto para os testes conduzirem a fila com um relógio próprio — esperar o backoff
    /// de verdade tornaria a suíte lenta sem provar nada a mais.
    /// </summary>
    public async Task<bool> RunNextAsync(DateTimeOffset now, CancellationToken ct)
    {
        PendingWrite? next;
        lock (_gate) next = _writes.FirstOrDefault(w => WritePolicy.IsDue(w, now));

        if (next is null) return false;

        WriteOutcome outcome;

        try
        {
            outcome = await _execute(next, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Executor que estoura em vez de devolver resultado ainda é uma falha da escrita, não
            // do app. Sem resposta HTTP, conta como passageira.
            outcome = WriteOutcome.Failed(null, ex.Message);
        }

        bool drained;

        lock (_gate)
        {
            var index = _writes.FindIndex(w => w.Id == next.Id);

            // Descartada pelo usuário enquanto subia: o resultado não interessa mais.
            if (index < 0) return true;

            if (outcome.Success) _writes.RemoveAt(index);
            else _writes[index] = WritePolicy.AfterFailure(
                _writes[index], outcome.HttpStatus, outcome.Message, now);

            drained = outcome.Success && !_writes.Any(w => WritePolicy.IsDue(w, now));
        }

        Announce();

        if (drained) Drained?.Invoke(this, EventArgs.Empty);

        return true;
    }

    /// <summary>
    /// Grava e avisa, sempre juntos.
    /// <para>
    /// Estavam separados, e <c>Discard</c> gravava sem avisar: a intenção saía da fila e a linha
    /// continuava na tela até algo não relacionado forçar um desenho. Um teste visual pegou o que
    /// os unitários não viam, porque eles não observam o evento. <b>Toda</b> mudança de fila passa
    /// por aqui agora — é o que impede a mesma armadilha no próximo método que alguém acrescentar.
    /// </para>
    /// </summary>
    private void Announce()
    {
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.Now;

                if (await RunNextAsync(now, ct)) continue;

                // Nada vencido. Dormir até o backoff mais próximo, ou até alguém chamar — nunca
                // girar em laço apertado (regra 9).
                var wait = NextWait(now);

                if (wait is { } delay) await _signal.WaitAsync(delay, ct);
                else await _signal.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A bomba não pode morrer: sem ela a fila para de escoar em silêncio, que é
                // exatamente o defeito que esta fila existe para consertar.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Quanto falta para a próxima tentativa agendada, ou <c>null</c> se não há nenhuma.</summary>
    private TimeSpan? NextWait(DateTimeOffset now)
    {
        lock (_gate)
        {
            var next = _writes
                .Where(w => w.State == WriteState.Pending && w.NextAttemptAt is not null)
                .Select(w => w.NextAttemptAt!.Value)
                .DefaultIfEmpty(DateTimeOffset.MaxValue)
                .Min();

            if (next == DateTimeOffset.MaxValue) return null;

            var wait = next - now;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
    }

    private void Persist() => _store.Save(Pending);

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _signal.Dispose();
    }
}
