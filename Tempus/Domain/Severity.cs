namespace Tempus.Domain;

/// <summary>
/// Ver <c>docs/SEVERITY.md</c> §1. Existem exatamente quatro níveis —
/// não adicionar um quinto sem revisar aquele documento.
/// <para>
/// Vive no domínio, e não na shell, porque é o vocabulário do modelo e não da tela: os sinais
/// (§2) produzem severidade, a arbitragem (§4) escolhe uma, e só então a barra a pinta. Enquanto
/// morou em <c>Tempus.Shell</c> nada disso podia existir sem o domínio depender da UI — o
/// contrário da regra 8.
/// </para>
/// </summary>
internal enum Severity
{
    Calm = 0,
    Info = 1,
    Attention = 2,
    Critical = 3,
}
