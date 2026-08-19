using System.IO;
using Tempus.Domain;
using Tempus.Sync;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// A fila de escritas conduzida à mão, com relógio próprio: esperar o backoff de verdade tornaria
/// a suíte dez segundos mais lenta sem provar nada a mais.
/// </summary>
public class WriteQueueTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 19, 10, 0, 0, TimeSpan.FromHours(-3));

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"tempus-tests-{Guid.NewGuid():n}");

    private PendingWriteStore Store() => new(_directory);

    private static PendingWrite Create(string title = "Nova tarefa") =>
        PendingWrite.For(WriteKind.Create, Now) with { Title = title, ListId = "L" };

    /// <summary>Executor que registra o que recebeu e devolve o que o teste mandar.</summary>
    private sealed class Fake
    {
        private readonly Queue<WriteOutcome> _answers = new();

        public List<PendingWrite> Seen { get; } = [];

        public Fake Then(WriteOutcome outcome)
        {
            _answers.Enqueue(outcome);
            return this;
        }

        public Task<WriteOutcome> ApplyAsync(PendingWrite write, CancellationToken ct)
        {
            Seen.Add(write);
            return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : WriteOutcome.Ok);
        }
    }

    // ---------------------------------------------------------------- caminho feliz

    [Fact]
    public async Task Escrita_que_sobe_sai_da_fila()
    {
        var fake = new Fake();
        using var queue = new WriteQueue(fake.ApplyAsync, Store());

        queue.Enqueue(Create());
        Assert.True(await queue.RunNextAsync(Now, default));

        Assert.Empty(queue.Pending);
        Assert.Single(fake.Seen);
    }

    [Fact]
    public async Task Fila_vazia_nao_tem_o_que_fazer()
    {
        using var queue = new WriteQueue(new Fake().ApplyAsync, Store());

        Assert.False(await queue.RunNextAsync(Now, default));
    }

    [Fact]
    public async Task A_ordem_e_de_chegada()
    {
        var fake = new Fake();
        using var queue = new WriteQueue(fake.ApplyAsync, Store());

        queue.Enqueue(Create("primeira"));
        queue.Enqueue(Create("segunda"));

        await queue.RunNextAsync(Now, default);
        await queue.RunNextAsync(Now, default);

        Assert.Equal(["primeira", "segunda"], fake.Seen.Select(w => w.Title));
    }

    // ---------------------------------------------------------------- falha e repetição

    [Fact]
    public async Task Falha_passageira_espera_o_backoff_antes_de_repetir()
    {
        var fake = new Fake().Then(WriteOutcome.Failed(null, "rede"));
        using var queue = new WriteQueue(fake.ApplyAsync, Store());

        queue.Enqueue(Create());
        await queue.RunNextAsync(Now, default);

        // Ainda na fila, mas não vencida: repetir na hora seria bater na mesma porta fechada.
        Assert.Single(queue.Pending);
        Assert.False(await queue.RunNextAsync(Now, default));
        Assert.True(await queue.RunNextAsync(Now.AddSeconds(3), default));
    }

    [Fact]
    public async Task Depois_de_tres_falhas_para_de_tentar_e_avisa()
    {
        var fake = new Fake();
        for (var i = 0; i < WritePolicy.MaxAttempts; i++) fake.Then(WriteOutcome.Failed(null, "rede"));

        using var queue = new WriteQueue(fake.ApplyAsync, Store());
        queue.Enqueue(Create());

        var clock = Now;
        for (var i = 0; i < WritePolicy.MaxAttempts; i++)
        {
            await queue.RunNextAsync(clock, default);
            clock = clock.AddMinutes(1);
        }

        var write = Assert.Single(queue.Pending);
        Assert.Equal(WriteState.Failed, write.State);
        Assert.True(queue.HasFailures);

        // Falhada nunca mais é tentada sozinha — a decisão passa a ser do usuário.
        Assert.False(await queue.RunNextAsync(clock.AddHours(1), default));
    }

    [Fact]
    public async Task Falha_permanente_desiste_na_primeira()
    {
        var fake = new Fake().Then(WriteOutcome.Failed(404, "not found"));
        using var queue = new WriteQueue(fake.ApplyAsync, Store());

        queue.Enqueue(Create());
        await queue.RunNextAsync(Now, default);

        Assert.Equal(WriteState.Failed, queue.Pending.Single().State);
        Assert.Single(fake.Seen);
    }

    /// <summary>Executor que estoura ainda é falha da escrita, não do app.</summary>
    [Fact]
    public async Task Excecao_do_executor_vira_falha_passageira()
    {
        using var queue = new WriteQueue(
            (_, _) => throw new InvalidOperationException("boom"), Store());

        queue.Enqueue(Create());
        await queue.RunNextAsync(Now, default);

        var write = queue.Pending.Single();
        Assert.Equal(WriteState.Pending, write.State);
        Assert.Equal(1, write.Attempts);
    }

    // ---------------------------------------------------------------- gestos do usuário

    [Fact]
    public async Task Tentar_de_novo_devolve_a_escrita_ao_jogo()
    {
        var fake = new Fake().Then(WriteOutcome.Failed(404, "não existe"));
        using var queue = new WriteQueue(fake.ApplyAsync, Store());

        queue.Enqueue(Create());
        await queue.RunNextAsync(Now, default);

        queue.Retry(queue.Pending.Single().Id);

        Assert.True(await queue.RunNextAsync(Now, default));
        Assert.Empty(queue.Pending);
    }

    [Fact]
    public async Task Descartar_tira_da_fila_sem_chamar_ninguem()
    {
        var fake = new Fake();
        using var queue = new WriteQueue(fake.ApplyAsync, Store());

        queue.Enqueue(Create());
        queue.Discard(queue.Pending.Single().Id);

        Assert.Empty(queue.Pending);
        Assert.False(await queue.RunNextAsync(Now, default));
        Assert.Empty(fake.Seen);
    }

    // ---------------------------------------------------------------- aviso à tela

    /// <summary>
    /// Regressão de um defeito que só um teste visual pegou: <c>Discard</c> gravava sem avisar, e
    /// a linha continuava na tela depois de descartada. Toda mudança de fila tem que avisar, senão
    /// a tela mente — que é justamente o que esta fila existe para consertar.
    /// </summary>
    [Fact]
    public async Task Toda_mudanca_de_fila_avisa_a_tela()
    {
        var fake = new Fake().Then(WriteOutcome.Failed(404, "sumiu"));
        using var queue = new WriteQueue(fake.ApplyAsync, Store());

        var avisos = 0;
        queue.Changed += (_, _) => avisos++;

        queue.Enqueue(Create());
        Assert.Equal(1, avisos);

        await queue.RunNextAsync(Now, default);
        Assert.Equal(2, avisos);

        var id = queue.Pending.Single().Id;

        queue.Retry(id);
        Assert.Equal(3, avisos);

        queue.Discard(id);
        Assert.Equal(4, avisos);
    }

    [Fact]
    public void Confirmar_sem_nada_a_dispensar_nao_avisa_a_toa()
    {
        using var queue = new WriteQueue(new Fake().ApplyAsync, Store());

        queue.Enqueue(Create("Alguma coisa"));

        var avisos = 0;
        queue.Changed += (_, _) => avisos++;

        queue.Confirm([new TaskItem { Id = "t1", Title = "Outra coisa" }]);

        Assert.Equal(0, avisos);
    }

    // ---------------------------------------------------------------- confirmação

    [Fact]
    public void Retrato_que_ja_reflete_a_intencao_a_dispensa()
    {
        using var queue = new WriteQueue(new Fake().ApplyAsync, Store());

        queue.Enqueue(Create("Revisar contrato"));
        queue.Confirm([new TaskItem { Id = "t9", Title = "Revisar contrato" }]);

        Assert.Empty(queue.Pending);
    }

    // ---------------------------------------------------------------- persistência

    /// <summary>
    /// O caso que motivou gravar a fila: rede caída, tarefa digitada, app fechado. O título só
    /// existe dentro da intenção, e sem disco ele morre ali.
    /// </summary>
    [Fact]
    public async Task Escrita_pendente_sobrevive_ao_restart()
    {
        var fake = new Fake().Then(WriteOutcome.Failed(null, "rede"));

        using (var queue = new WriteQueue(fake.ApplyAsync, Store()))
        {
            queue.Enqueue(Create("Não me perca"));
            await queue.RunNextAsync(Now, default);
        }

        // Outra instância, mesmo diretório: é o que acontece ao reabrir o app.
        var depois = new Fake();
        using var reaberta = new WriteQueue(depois.ApplyAsync, Store());

        var write = Assert.Single(reaberta.Pending);
        Assert.Equal("Não me perca", write.Title);

        // Volta com tentativas zeradas: o app caiu, mas a rede pode ter voltado.
        Assert.Equal(0, write.Attempts);
        Assert.True(await reaberta.RunNextAsync(Now, default));
    }

    [Fact]
    public async Task Falhada_continua_falhada_depois_do_restart()
    {
        var fake = new Fake().Then(WriteOutcome.Failed(404, "sumiu"));

        using (var queue = new WriteQueue(fake.ApplyAsync, Store()))
        {
            queue.Enqueue(Create());
            await queue.RunNextAsync(Now, default);
        }

        using var reaberta = new WriteQueue(new Fake().ApplyAsync, Store());

        Assert.Equal(WriteState.Failed, reaberta.Pending.Single().State);
    }

    [Fact]
    public async Task Fila_vazia_nao_deixa_arquivo_para_tras()
    {
        using var queue = new WriteQueue(new Fake().ApplyAsync, Store());

        queue.Enqueue(Create());
        await queue.RunNextAsync(Now, default);

        Assert.False(File.Exists(Path.Combine(_directory, "pending-writes.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
