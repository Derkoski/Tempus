using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tempus.Domain;
using Tempus.Interop;

namespace Tempus.Shell;

/// <summary>
/// Superfície S3 do SPEC: a timeline do dia.
/// <para>
/// O elemento que carrega o valor de produto não são os eventos — é o que aparece
/// <b>entre</b> eles. Ver um "sem intervalo" antes da próxima reunião é o que permite não
/// estourar a atual, e é a informação que nenhuma das ferramentas do Google mostra.
/// </para>
/// </summary>
internal partial class AgendaPanel : Window
{
    private readonly PanelHost _host;
    private readonly Palette _palette;

    public AgendaPanel(Palette palette, NativeMethods.RECT anchor)
    {
        _palette = palette;
        InitializeComponent();

        _host = new PanelHost(this) { Anchor = anchor };
        _host.CloseRequested += (_, _) => Dismissed?.Invoke(this, EventArgs.Empty);

        ApplyPalette();
    }

    /// <summary>Usuário clicou num evento com Meet. Carrega a URL.</summary>
    public event EventHandler<string>? MeetingActivated;

    /// <summary>Perdeu o foco ou levou <c>Esc</c>: a superfície deve fechar este painel.</summary>
    public event EventHandler? Dismissed;

    public void ShowAt(NativeMethods.RECT anchor) => _host.ShowAt(anchor);

    public void Render(IReadOnlyList<AgendaItem> agenda, DateTimeOffset now)
    {
        var events = agenda
            .Where(e => !e.IsAllDay) // dia inteiro é marcador, não compromisso (SEVERITY.md §2.1)
            .OrderBy(e => e.Start)
            .ToList();

        HeaderCount.Text = events.Count switch
        {
            0 => "sem compromissos",
            1 => "1 compromisso",
            _ => $"{events.Count} compromissos",
        };

        Timeline.Children.Clear();

        if (events.Count == 0)
        {
            Timeline.Children.Add(new TextBlock
            {
                Text = "Nenhuma reunião hoje.",
                Margin = new Thickness(6, 14, 6, 18),
                Foreground = new SolidColorBrush(_palette.Muted),
            });
            RepositionAfterLayout();
            return;
        }

        var next = events.FirstOrDefault(e => e.Start > now);

        for (var i = 0; i < events.Count; i++)
        {
            var current = events[i];
            Timeline.Children.Add(BuildEventRow(current, now, isNext: current == next));

            if (i + 1 < events.Count)
                Timeline.Children.Add(BuildGapRow(new AgendaGap(events[i + 1].Start - current.End)));
        }

        RepositionAfterLayout();
    }

    private UIElement BuildEventRow(AgendaItem item, DateTimeOffset now, bool isNext)
    {
        var isRunning = item.IsRunningAt(now);
        var isPast = item.HasEndedBy(now);

        var row = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 7, 8, 7),
            Background = Brushes.Transparent,
            Cursor = item.Conference is null ? Cursors.Arrow : Cursors.Hand,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // faixa de estado
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // horário
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // resposta ao convite
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // selo de estado

        // Faixa vertical marcando o evento em curso.
        var marker = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 1, 9, 1),
            Background = new SolidColorBrush(isRunning
                ? _palette.AttentionBackground
                : isNext ? _palette.InfoBackground : Colors.Transparent),
        };
        Grid.SetColumn(marker, 0);

        var time = new TextBlock
        {
            Text = $"{item.Start.ToLocalTime():HH:mm}–{item.End.ToLocalTime():HH:mm}",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            FontWeight = isRunning ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(isPast ? _palette.Muted : _palette.BarForeground),
        };
        Grid.SetColumn(time, 1);

        var title = new TextBlock
        {
            Text = item.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = isRunning ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(isPast ? _palette.Muted : _palette.BarForeground),
        };

        // O serviço da call é o fundo do título (D-017). Fica na coluna estrelada, então o
        // Border acompanha a largura e o texto dentro continua truncando com reticências.
        var titleHost = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(item.Conference is null ? 0 : 6, 2, 6, 2),
            Margin = new Thickness(item.Conference is null ? 0 : -6, 0, 0, 0),
            Background = new SolidColorBrush(ProviderTint(item.Conference, isPast)),
            Child = title,
        };
        Grid.SetColumn(titleHost, 3);

        grid.Children.Add(marker);
        grid.Children.Add(time);
        grid.Children.Add(titleHost);

        if (BuildRsvpMark(item.Rsvp, isPast) is { } mark)
        {
            Grid.SetColumn(mark, 2);
            grid.Children.Add(mark);
        }

        var badge = BuildBadge(isRunning, isNext, isPast);
        if (badge is not null)
        {
            Grid.SetColumn(badge, 4);
            grid.Children.Add(badge);
        }

        row.Child = grid;

        if (item.Conference is { } conference)
        {
            var hover = new SolidColorBrush(_palette.RowHover);
            row.MouseEnter += (_, _) => row.Background = hover;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                MeetingActivated?.Invoke(this, conference.Url);
            };
            row.ToolTip = $"Clique para entrar no {conference.ProviderName}";
        }

        return row;
    }

    /// <summary>
    /// Fundo do título na cor do serviço (D-017). Tingido, e não saturado: quase todo evento tem
    /// call, e chapado o painel viraria uma parede de azul e verde onde nada se destaca.
    /// Transparente quando não há call — a ausência já diz "presencial".
    /// </summary>
    private Color ProviderTint(Conference? conference, bool isPast)
    {
        if (conference is null) return Colors.Transparent;

        var c = _palette.ForProvider(conference.Provider);

        // Evento passado desbota junto com o resto da linha: o que já acabou não deve competir
        // com o que ainda vai acontecer.
        var alpha = (byte)(isPast ? 0x22 : 0x4D);

        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    /// <summary>
    /// Sua resposta ao convite (D-018).
    /// <para>
    /// Três <b>formas distintas</b> — ✓, ?, ● — e não variações de um mesmo círculo. A primeira
    /// versão distinguia por preenchimento (cheio vs. anel vazado) para economizar cor, e falhou
    /// no uso real: a 9px, cheio e vazado só se distinguem de perto. Forma diferente se lê de
    /// relance; preenchimento, não.
    /// </para>
    /// <para>
    /// Só o pendente colore. Aceito é o estado normal e não precisa competir com nada; "não
    /// respondi" é o único que exige ação, e usa o âmbar de atenção do próprio modelo de
    /// severidade — a mesma cor querendo dizer a mesma coisa.
    /// </para>
    /// </summary>
    private UIElement? BuildRsvpMark(Rsvp rsvp, bool isPast)
    {
        if (rsvp is Rsvp.None) return null;

        var margin = new Thickness(0, 0, 9, 0);

        UIElement mark = rsvp switch
        {
            Rsvp.NeedsAction => new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(1, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(_palette.AttentionBackground),
            },

            // E73E é o mesmo CheckMark do contador de tarefas — check já significa "resolvido"
            // no resto da barra, e repetir o símbolo é coerência, não preguiça.
            Rsvp.Accepted => new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 10,
                Margin = margin,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(_palette.Muted),
            },

            _ => new TextBlock
            {
                Text = "?",
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Margin = margin,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(_palette.Muted),
            },
        };

        if (mark is FrameworkElement fe)
        {
            fe.Opacity = isPast ? 0.4 : 1.0;
            fe.ToolTip = rsvp switch
            {
                Rsvp.Accepted => "Você aceitou",
                Rsvp.Tentative => "Você marcou como talvez",
                _ => "Você ainda não respondeu",
            };
        }

        return mark;
    }

    private UIElement? BuildBadge(bool isRunning, bool isNext, bool isPast)
    {
        var (text, background, foreground) = (isRunning, isNext, isPast) switch
        {
            (true, _, _) => ("agora", _palette.AttentionBackground, _palette.AttentionForeground),
            (_, true, _) => ("próxima", _palette.InfoBackground, _palette.InfoForeground),
            _ => ("", Colors.Transparent, Colors.Transparent),
        };

        if (text.Length == 0) return null;

        return new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(background),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(foreground),
            },
        };
    }

    /// <summary>
    /// A linha entre dois eventos. Sem intervalo e sobreposição ganham cor de alerta porque são
    /// exatamente as situações que produzem o vermelho principal do produto
    /// (<c>MeetingRanIntoNext</c>); um intervalo folgado fica discreto.
    /// </summary>
    private UIElement BuildGapRow(AgendaGap gap)
    {
        var tense = gap.IsOverlap || gap.IsBackToBack;
        var accent = gap.IsOverlap ? _palette.CriticalBackground : _palette.AttentionBackground;
        var color = tense ? accent : _palette.Muted;

        var grid = new Grid { Margin = new Thickness(6, 1, 8, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Alinha com a faixa vertical dos eventos, mantendo a coluna de tempo intacta.
        var stem = new Border
        {
            Width = 3,
            Height = tense ? 14.0 : 10.0,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 9, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(color) { Opacity = tense ? 0.9 : 0.35 },
        };
        Grid.SetColumn(stem, 0);

        var label = new TextBlock
        {
            Text = gap.Label,
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = tense ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(color),
        };
        Grid.SetColumn(label, 1);

        grid.Children.Add(stem);
        grid.Children.Add(label);
        return grid;
    }

    private void RepositionAfterLayout() => Dispatcher.BeginInvoke(
        new Action(() => _host.Reposition()), System.Windows.Threading.DispatcherPriority.Loaded);

    private void ApplyPalette()
    {
        Background = Brushes.Transparent;
        Shell.Background = new SolidColorBrush(_palette.PanelBackground);
        Shell.BorderBrush = new SolidColorBrush(_palette.PanelBorder);

        HeaderText.Foreground = new SolidColorBrush(_palette.BarForeground);
        HeaderCount.Foreground = new SolidColorBrush(_palette.Muted);
    }
}
