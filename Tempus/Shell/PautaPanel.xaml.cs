using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tempus.Domain;
using Tempus.Interop;

namespace Tempus.Shell;

/// <summary>
/// Superfície S5 do SPEC: a pauta de uma reunião — os assuntos que você quer levantar nela (D-052).
/// <para>
/// Deliberadamente <b>mais pobre</b> que o painel de tarefas. Nada de vencimento, de agrupamento
/// nem de renomear: um assunto tem texto, e ou já foi dito ou não. A riqueza do S2 existe porque
/// tarefa tem prazo e concorre com outras; assunto vale por uma hora e concorre com três irmãos.
/// </para>
/// <para>
/// Riscar é concluir, no sentido literal da API — o que traz de graça a reversão do D-030: clicar
/// de novo devolve o assunto à lista dos não ditos.
/// </para>
/// </summary>
internal partial class PautaPanel : Window
{
    private readonly PanelHost _host;
    private readonly Palette _palette;

    private static readonly System.Globalization.CultureInfo Brasil = new("pt-BR");

    public PautaPanel(Palette palette, NativeMethods.RECT anchor)
    {
        _palette = palette;
        InitializeComponent();

        _host = new PanelHost(this) { Anchor = anchor };
        _host.CloseRequested += (_, _) => Dismissed?.Invoke(this, EventArgs.Empty);

        ApplyPalette();

        NewItemBox.TextChanged += (_, _) =>
            Placeholder.Visibility = string.IsNullOrEmpty(NewItemBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

        NewItemBox.KeyDown += OnNewItemKeyDown;
    }

    /// <summary>Novo assunto. Carrega o texto — a reunião quem sabe é a superfície.</summary>
    public event EventHandler<string>? ItemCreated;

    /// <summary>Riscar ou desriscar um assunto. Carrega o id.</summary>
    public event EventHandler<string>? ItemToggled;

    /// <summary>Excluir. Carrega o id. Não tem volta (D-024).</summary>
    public event EventHandler<string>? ItemDeleted;

    /// <summary>"Tentar de novo" numa escrita que falhou. Carrega o id da <b>intenção</b>.</summary>
    public event EventHandler<string>? WriteRetried;

    /// <summary>"Deixa pra lá": abandona a intenção. Carrega o id da <b>intenção</b>.</summary>
    public event EventHandler<string>? WriteDiscarded;

    /// <summary>Perdeu o foco ou levou <c>Esc</c>: a superfície deve fechar este painel.</summary>
    public event EventHandler? Dismissed;

    public void ShowAt(NativeMethods.RECT anchor) => _host.ShowAt(anchor);

    /// <summary>A reunião cuja pauta está aberta. O painel inteiro existe em relação a ela.</summary>
    public string? EventId { get; private set; }

    private IReadOnlyList<TaskRow> _last = [];

    /// <summary>
    /// Dado novo de fora — sync, escrita confirmada, painel reaberto.
    /// <para>
    /// A reunião é <b>navegação</b>, e por isso entra na construção do desenho e não muda a cada
    /// rodada: é a regra 12 do projeto, que nasceu de dois painéis que se desfaziam sozinhos
    /// (D-049).
    /// </para>
    /// </summary>
    public void Render(AgendaItem meeting, IReadOnlyList<TaskRow> items)
    {
        EventId = meeting.Id;

        HeaderText.Text = meeting.Title;
        HeaderWhen.Text = Quando(meeting);

        _last = items;
        Draw();
    }

    private static string Quando(AgendaItem meeting)
    {
        var start = meeting.Start.ToLocalTime();
        var hoje = DateOnly.FromDateTime(DateTime.Today);
        var dia = DateOnly.FromDateTime(start.Date);

        var quando = dia == hoje
            ? "hoje"
            : dia == hoje.AddDays(1)
                ? "amanhã"
                : Brasil.TextInfo.ToTitleCase(dia.ToString("ddd dd/MM", Brasil));

        return meeting.IsAllDay
            ? quando
            : $"{quando} · {start:HH:mm}–{meeting.End.ToLocalTime():HH:mm}";
    }

    private void Draw()
    {
        Items.Children.Clear();

        if (_last.Count == 0)
        {
            Items.Children.Add(new TextBlock
            {
                Text = "Nenhum assunto ainda. Escreva abaixo o que você quer levantar.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 14, 6, 18),
                Foreground = new SolidColorBrush(_palette.Muted),
            });
        }
        else
        {
            foreach (var row in _last) Items.Children.Add(BuildRow(row));
        }

        RepositionAfterLayout();
    }

    /// <summary>
    /// Uma linha da pauta. Mesma anatomia da linha de tarefa — caixa, texto, ✕ sob o ponteiro —
    /// para o gesto ser o mesmo que a mão já aprendeu no painel S2.
    /// </summary>
    private UIElement BuildRow(TaskRow entry)
    {
        var item = entry.Item;
        var dito = item.IsCompleted;

        var row = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 7, 8, 7),
            Background = Brushes.Transparent,
            Cursor = entry.IsPending ? Cursors.Arrow : Cursors.Hand,
            Opacity = entry.IsPending ? 0.55 : 1.0,
            ToolTip = dito ? "Clique para desmarcar" : "Clique quando já tiver falado",
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Caixa desenhada, não glifo de fonte: nenhuma dependência de qual Segoe está instalado.
        var box = new Border
        {
            Width = 15,
            Height = 15,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
            BorderThickness = new Thickness(dito ? 0 : 1.2),
            BorderBrush = new SolidColorBrush(_palette.Muted),
            Background = new SolidColorBrush(dito ? _palette.Muted : Colors.Transparent),
            Child = dito
                ? new TextBlock
                {
                    Text = "✓",
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(_palette.PanelBackground),
                }
                : null,
        };
        Grid.SetColumn(box, 0);

        var texto = new TextBlock
        {
            Text = item.Title,
            VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = dito ? TextDecorations.Strikethrough : null,
            Foreground = new SolidColorBrush(dito ? _palette.Muted : _palette.BarForeground),
        }.WrappingToTwoLines();
        Grid.SetColumn(texto, 1);

        grid.Children.Add(box);
        grid.Children.Add(texto);

        // Falha ocupa a coluna do meio-fim, como no painel de tarefas: naquele instante o que
        // importa é o que não subiu.
        if (entry.HasFailed)
        {
            var aviso = BuildRetryAction(entry);
            Grid.SetColumn(aviso, 2);
            grid.Children.Add(aviso);
        }

        var delete = BuildDeleteAction(entry, row);
        Grid.SetColumn(delete, 3);
        grid.Children.Add(delete);

        row.Child = grid;

        if (entry.IsPending) return row;

        var hover = new SolidColorBrush(_palette.RowHover);
        row.MouseEnter += (_, _) =>
        {
            row.Background = hover;
            if (delete.Tag is null) delete.Visibility = Visibility.Visible;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = Brushes.Transparent;

            // Hidden, e não Collapsed: a coluna já reservou o espaço, e recolher requebraria o
            // texto debaixo do ponteiro a cada passagem (D-051).
            if (delete.Tag is null) delete.Visibility = Visibility.Hidden;
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;

            // Falha pendente trava o gesto: duas intenções conflitantes sobre o mesmo assunto na
            // fila não teriam resposta certa.
            if (entry.HasFailed) return;

            ItemToggled?.Invoke(this, item.Id);
        };

        return row;
    }

    private FrameworkElement BuildRetryAction(TaskRow entry)
    {
        var write = entry.Write!;

        var pill = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(_palette.CriticalBackground),
            Cursor = Cursors.Hand,
            ToolTip = $"{write.Failure} — clique para tentar de novo",
            Child = new TextBlock
            {
                Text = "não salvou",
                FontSize = 11,
                Foreground = new SolidColorBrush(_palette.CriticalForeground),
            },
        };

        pill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true; // sem isto o clique borbulha e risca o assunto
            WriteRetried?.Invoke(this, write.Id);
        };

        return pill;
    }

    /// <summary>
    /// O ✕, com a mesma confirmação de dois cliques e o mesmo alvo de 44×26 do painel de tarefas.
    /// O motivo do tamanho está no D-024, e vale igual aqui: o vizinho maior risca o assunto, e
    /// errar por quinze pixels trocaria "excluir?" por "já falei".
    /// </summary>
    private FrameworkElement BuildDeleteAction(TaskRow entry, Border row)
    {
        var descartando = entry.HasFailed;
        var palavra = descartando ? "Descartar?" : "Excluir?";
        var ocioso = descartando ? "Descartar a alteração que não subiu" : "Excluir assunto";

        var host = new Border
        {
            MinWidth = 44,
            MinHeight = 26,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(10, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Hidden,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = ocioso,
        };

        var glifo = new TextBlock
        {
            Text = "✕",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(_palette.Muted),
        };

        host.Child = glifo;

        host.MouseEnter += (_, _) =>
        {
            if (host.Tag is null) glifo.Foreground = new SolidColorBrush(_palette.CriticalBackground);
        };
        host.MouseLeave += (_, _) =>
        {
            if (host.Tag is null) glifo.Foreground = new SolidColorBrush(_palette.Muted);
        };

        host.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;

            if (host.Tag is null)
            {
                host.Tag = "confirmando";
                host.Background = new SolidColorBrush(_palette.CriticalBackground);
                host.ToolTip = "Clique de novo para confirmar — sair da linha cancela";
                glifo.Text = palavra;
                glifo.Foreground = new SolidColorBrush(_palette.CriticalForeground);
                return;
            }

            if (descartando) WriteDiscarded?.Invoke(this, entry.Write!.Id);
            else ItemDeleted?.Invoke(this, entry.Item.Id);
        };

        row.MouseLeave += (_, _) =>
        {
            if (host.Tag is null) return;

            host.Tag = null;
            host.Background = Brushes.Transparent;
            host.ToolTip = ocioso;
            glifo.Text = "✕";
            glifo.Foreground = new SolidColorBrush(_palette.Muted);
        };

        return host;
    }

    private void OnNewItemKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;

        var texto = NewItemBox.Text.Trim();
        if (texto.Length == 0) return;

        NewItemBox.Clear();
        ItemCreated?.Invoke(this, texto);
    }

    private void RepositionAfterLayout() => Dispatcher.BeginInvoke(
        new Action(() => _host.Reposition()), System.Windows.Threading.DispatcherPriority.Loaded);

    private void ApplyPalette()
    {
        Background = Brushes.Transparent;
        Shell.Background = new SolidColorBrush(_palette.PanelBackground);
        Shell.BorderBrush = new SolidColorBrush(_palette.PanelBorder);

        HeaderText.Foreground = new SolidColorBrush(_palette.BarForeground);
        HeaderWhen.Foreground = new SolidColorBrush(_palette.Muted);

        ComposerBorder.BorderBrush = new SolidColorBrush(_palette.PanelBorder);
        Placeholder.Foreground = new SolidColorBrush(_palette.Muted);
        ComposerHint.Foreground = new SolidColorBrush(_palette.Muted);
        NewItemBox.Foreground = new SolidColorBrush(_palette.BarForeground);
        NewItemBox.CaretBrush = new SolidColorBrush(_palette.BarForeground);
    }
}
