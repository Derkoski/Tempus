using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tempus.Shell;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// O corte do título em duas linhas.
/// <para>
/// Único teste do repositório que toca WPF, e por um motivo: <c>MaxLines</c> não existe em WPF, o
/// corte é por <b>altura</b>, e altura depende da métrica da fonte. Um limite que "parece certo" na
/// máquina de hoje pode virar uma linha e meia noutra fonte de sistema, e o sintoma seria um título
/// cortado ao meio na horizontal — feio e sem explicação. Aqui a garantia é medida.
/// </para>
/// <para>
/// Precisa de STA porque <c>TextBlock</c> é <c>DispatcherObject</c>. Nada aqui abre janela.
/// </para>
/// </summary>
public class TitleTextTests
{
    private const double Larguras = 300; // largura típica da coluna do título no painel de 520

    /// <summary>Mede um título isolado, fora de qualquer janela, numa thread STA.</summary>
    private static Size Medir(string texto, double largura = Larguras)
    {
        var medida = new Size();

        var thread = new Thread(() =>
        {
            var bloco = new TextBlock
            {
                Text = texto,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 13,
            }.WrappingToTwoLines();

            bloco.Measure(new Size(largura, double.PositiveInfinity));
            medida = bloco.DesiredSize;
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return medida;
    }

    [Fact]
    public void Titulo_curto_ocupa_uma_linha()
    {
        Assert.Equal(18, Medir("Anotar ideias").Height, precision: 1);
    }

    [Fact]
    public void Titulo_medio_quebra_para_duas_linhas()
    {
        var altura = Medir(
            "Levantar com o time de dados quais tabelas do ETL antigo ainda são lidas").Height;

        Assert.Equal(36, altura, precision: 1);
    }

    /// <summary>
    /// O que impede o painel de virar um documento: um título patológico — uma URL colada, uma
    /// frase inteira — não pode empurrar a lista para fora da tela.
    /// </summary>
    [Fact]
    public void Titulo_gigante_para_em_duas_linhas()
    {
        var altura = Medir(new string('a', 40) + " " + string.Join(" ", Enumerable.Repeat(
            "documentar o passo a passo de reprocessamento manual da carga noturna", 12))).Height;

        Assert.Equal(36, altura, precision: 1);
    }

    /// <summary>
    /// A altura da linha é <b>fixa</b>, e não a natural da fonte. Sem isto, um glifo mais alto no
    /// meio do texto engordaria a linha, a segunda não caberia mais no limite, e o título voltaria
    /// a ser de uma linha só — sem nada na tela explicando por quê.
    /// </summary>
    [Fact]
    public void Acento_e_descendente_nao_engordam_a_linha()
    {
        var simples = Medir("consolidar relatorio mensal e enviar para o time responsavel agora");
        var carregado = Medir("Ãj ÇÂpq consolidar relatório mensal e enviá-lo à gestão hoje jjj");

        Assert.Equal(simples.Height, carregado.Height, precision: 1);
        Assert.Equal(36, carregado.Height, precision: 1);
    }
}
