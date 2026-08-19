using Tempus.Domain;
using Xunit;

namespace Tempus.Tests;

/// <summary>Quando repetir uma escrita e quando parar de tentar.</summary>
public class WritePolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 19, 10, 0, 0, TimeSpan.FromHours(-3));

    private static PendingWrite Write() =>
        PendingWrite.For(WriteKind.Complete, Now) with { TaskId = "t1", ListId = "L" };

    // ---------------------------------------------------------------- classificação

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(410)]
    public void Requisicao_invalida_ou_alvo_inexistente_e_permanente(int status) =>
        Assert.Equal(WriteFailureKind.Permanent, WritePolicy.Classify(status));

    [Theory]
    [InlineData(null)]  // rede caída: nem houve resposta
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void Rede_quota_e_erro_de_servidor_sao_passageiros(int? status) =>
        Assert.Equal(WriteFailureKind.Transient, WritePolicy.Classify(status));

    /// <summary>
    /// O Google usa 403 para permissão negada <b>e</b> para 'rateLimitExceeded'. Tratar como
    /// permanente descartaria escrita legítima numa rajada; três tentativas custam pouco.
    /// </summary>
    [Fact]
    public void O_403_ambiguo_do_google_e_tratado_como_passageiro() =>
        Assert.Equal(WriteFailureKind.Transient, WritePolicy.Classify(403));

    // ---------------------------------------------------------------- ciclo de tentativas

    [Fact]
    public void Falha_passageira_agenda_a_proxima_com_espera_crescente()
    {
        var write = Write();
        var esperas = new List<TimeSpan>();

        for (var i = 0; i < WritePolicy.MaxAttempts; i++)
        {
            write = WritePolicy.AfterFailure(write, null, null, Now);
            if (write.NextAttemptAt is { } at) esperas.Add(at - Now);
        }

        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)], esperas);
    }

    [Fact]
    public void Depois_de_tres_tentativas_desiste_e_espera_o_usuario()
    {
        var write = Write();

        for (var i = 0; i < WritePolicy.MaxAttempts; i++)
            write = WritePolicy.AfterFailure(write, null, null, Now);

        Assert.Equal(WriteState.Failed, write.State);
        Assert.Equal(WritePolicy.MaxAttempts, write.Attempts);
        Assert.Null(write.NextAttemptAt);
    }

    [Fact]
    public void Falha_permanente_desiste_na_primeira()
    {
        var write = WritePolicy.AfterFailure(Write(), 404, null, Now);

        Assert.Equal(WriteState.Failed, write.State);
        Assert.Equal(1, write.Attempts);
        Assert.Contains("não existe mais", write.Failure);
    }

    [Fact]
    public void Falha_sem_resposta_diz_que_e_conexao()
    {
        var write = WritePolicy.AfterFailure(Write(), null, null, Now);

        Assert.Equal("Sem conexão com o Google", write.Failure);
    }

    // ---------------------------------------------------------------- vencimento e revival

    [Fact]
    public void Escrita_nova_esta_vencida_na_hora() =>
        Assert.True(WritePolicy.IsDue(Write(), Now));

    [Fact]
    public void Escrita_esperando_backoff_nao_esta_vencida()
    {
        var write = WritePolicy.AfterFailure(Write(), null, null, Now);

        Assert.False(WritePolicy.IsDue(write, Now.AddSeconds(1)));
        Assert.True(WritePolicy.IsDue(write, Now.AddSeconds(3)));
    }

    [Fact]
    public void Falhada_nunca_vence_sozinha()
    {
        var write = WritePolicy.AfterFailure(Write(), 404, null, Now);

        Assert.False(WritePolicy.IsDue(write, Now.AddHours(5)));
    }

    /// <summary>"Tentar de novo" é ordem do usuário: zera o histórico e vale para agora.</summary>
    [Fact]
    public void Repetir_por_ordem_do_usuario_zera_o_historico()
    {
        var write = WritePolicy.AfterFailure(Write(), 404, null, Now);
        var revivida = WritePolicy.Revive(write);

        Assert.Equal(WriteState.Pending, revivida.State);
        Assert.Equal(0, revivida.Attempts);
        Assert.Null(revivida.Failure);
        Assert.True(WritePolicy.IsDue(revivida, Now));
    }
}
