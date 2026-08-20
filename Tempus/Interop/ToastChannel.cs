using System.Diagnostics;
using System.Security;
using System.Text;
using Tempus.Domain;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Tempus.Interop;

/// <summary>
/// A saída de toast do Windows. Não decide nada: quem decide é <c>ToastPolicy</c>, que é pura e
/// testável. Aqui só se monta o XML, se entrega ao shell e se escuta a volta.
/// <para>
/// <b>Ativação sem servidor COM (D-039).</b> Três tipos de botão, e a diferença entre eles é de
/// quem depende. <c>Join</c> sai como <c>activationType="protocol"</c> — o Windows dá
/// <c>ShellExecute</c> na URL, sem passar por nós, então entrar na call funciona mesmo que todo o
/// resto deste arquivo esteja quebrado. <c>Dismiss</c> é <c>activationType="system"</c>, tratado
/// pelo shell. Só o <c>Acknowledge</c> e o clique no corpo precisam voltar ao processo, e para
/// isso basta o evento <see cref="ToastNotification.Activated"/> do objeto vivo — o caminho
/// documentado para app não empacotado, sem CLSID e sem nada a desregistrar na desinstalação.
/// </para>
/// <para>
/// A contrapartida: esse evento só chega enquanto o processo está de pé e ainda segura o objeto.
/// Daí o <see cref="_live"/> — soltar a referência é perder o clique em silêncio.
/// </para>
/// </summary>
internal sealed class ToastChannel
{
    /// <summary>
    /// Depois de tantas falhas seguidas o canal se desliga. Notificação é acessório: insistir a
    /// cada 5 segundos numa API que está recusando gastaria bateria para não mostrar nada.
    /// </summary>
    private const int MaxFailures = 3;

    /// <summary>
    /// Quanto tempo o aviso sobrevive na Central de Ações, quando o pedido não diz outra coisa. A
    /// barra é a superfície persistente — enquanto o vermelho não for reconhecido ele continua lá,
    /// colorido. A cópia na Central é conveniência para quem estava longe da tela, e não precisa
    /// atravessar o dia.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    /// <summary>
    /// Os toasts ainda no ar, por etiqueta. Duas razões, e as duas são obrigatórias: sem a
    /// referência viva o evento de clique não chega, e sem o objeto original não dá para
    /// <c>Hide</c> um aviso fixo que já foi resolvido em outro lugar.
    /// </summary>
    private readonly Dictionary<string, ToastNotification> _live = [];

    private ToastNotifier? _notifier;
    private int _failures;

    /// <summary>
    /// Um botão foi clicado, ou o corpo do toast. O argumento é o que a política pôs lá.
    /// <para>
    /// <b>Chega numa thread do WinRT</b>, não na de UI — quem escuta precisa voltar pelo
    /// dispatcher antes de tocar em qualquer coisa.
    /// </para>
    /// </summary>
    public event EventHandler<string>? Activated;

    /// <summary>False quando o canal desistiu ou nunca chegou a abrir.</summary>
    public bool IsAvailable => _failures < MaxFailures;

    /// <summary>O que impediu a última tentativa, para a sonda ter o que dizer.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Prepara a identidade e abre o notificador. Chamar uma vez, na subida. Devolve <c>false</c>
    /// se o Windows não vai entregar nada — e aí ninguém perde tempo montando XML depois.
    /// </summary>
    public bool Open()
    {
        if (!AppIdentity.EnsureShortcut())
        {
            LastError = AppIdentity.LastError;
            _failures = MaxFailures;
            return false;
        }

        try
        {
            _notifier = ToastNotificationManager.CreateToastNotifier(AppIdentity.Aumid);
            LastError = null;
            return true;
        }
        catch (Exception e)
        {
            LastError = $"{e.GetType().Name}: {e.Message}";
            Debug.WriteLine($"Canal de toast indisponível: {LastError}");
            _failures = MaxFailures;
            return false;
        }
    }

    public void Show(ToastRequest request)
    {
        if (_notifier is null || !IsAvailable) return;

        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(Compose(request));

            var notification = new ToastNotification(xml)
            {
                Tag = request.Tag,
                Group = "tempus",
                ExpirationTime = request.ExpiresAt ?? DateTimeOffset.Now + Lifetime,
            };

            notification.Activated += OnActivated;

            // Sumiu da tela por qualquer motivo: para de ser candidato a Hide, e a referência que
            // só existia para o clique chegar deixa de ser necessária.
            notification.Dismissed += (_, _) => Forget(request.Tag);
            notification.Failed += (_, _) => Forget(request.Tag);

            // Antes do Show, e substituindo o que houver: a mesma etiqueta é usada de propósito
            // pelas duas interrupções do mesmo assunto, e a segunda toma o lugar da primeira.
            Forget(request.Tag);
            _live[request.Tag] = notification;

            _notifier.Show(notification);
            _failures = 0;
        }
        catch (Exception e)
        {
            // Notificações desligadas por política, Central indisponível, XML recusado: nada disso
            // pode derrubar a barra. A cor continua contando a mesma história sozinha.
            LastError = $"{e.GetType().Name}: {e.Message}";
            Debug.WriteLine($"Toast não entregue: {LastError}");
            _failures++;
        }
    }

    /// <summary>
    /// Retira o aviso <b>da tela e da Central</b>. Chamado quando o usuário reconhece na barra, ou
    /// quando a situação acabou sozinha: manter um vermelho já resolvido é mostrar dado velho com
    /// cara de atual, que é justamente o que a regra 10 proíbe.
    /// <para>
    /// As duas metades são necessárias. <c>History.Remove</c> tira da Central e <b>não</b> tira da
    /// tela — com toast fixo isso deixaria o aviso pendurado até alguém clicar nele.
    /// </para>
    /// </summary>
    public void Withdraw(string tag)
    {
        if (_notifier is null || !IsAvailable) return;

        if (_live.Remove(tag, out var live))
        {
            try
            {
                _notifier.Hide(live);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Toast não retirado da tela: {e.Message}");
            }
        }

        try
        {
            ToastNotificationManager.History.Remove(tag, "tempus", AppIdentity.Aumid);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Toast não retirado da Central: {e.Message}");
        }
    }

    /// <summary>As etiquetas ainda no ar, para quem precisa decidir o que retirar.</summary>
    public IReadOnlyCollection<string> LiveTags => [.. _live.Keys];

    private void Forget(string tag) => _live.Remove(tag);

    private void OnActivated(ToastNotification sender, object args)
    {
        // O corpo do toast chega sem argumento em algumas versões; o botão sempre traz o seu.
        var argument = args is ToastActivatedEventArgs activated ? activated.Arguments : null;

        Forget(sender.Tag);
        Activated?.Invoke(this, argument ?? string.Empty);
    }

    /// <summary>
    /// O XML do toast. <c>ToastGeneric</c> é o único template que ainda vale no Windows 10+; os
    /// antigos continuam funcionando mas são convertidos por baixo.
    /// </summary>
    private static string Compose(ToastRequest request)
    {
        var toast = new StringBuilder();

        // scenario="reminder" é o que faz o aviso ficar pré-expandido e esperar por um gesto em vez
        // de sumir sozinho. Só vale acompanhado de pelo menos um botão — sem isso o Windows o trata
        // como toast comum, sem reclamar. A política garante o botão; ver ToastRequest.StaysOnScreen.
        toast.Append("<toast");
        if (request.StaysOnScreen) toast.Append(" scenario=\"reminder\"");

        // O clique no corpo faz o que o clique na barra faz (D-038): reconhece se houver o que
        // reconhecer, senão abre a agenda do dia. Quem decide isso é o App, a partir deste argumento.
        toast.Append(" activationType=\"foreground\" launch=\"")
             .Append(Escape($"body|{request.Occurrence}"))
             .Append("\">");

        toast.Append("<visual><binding template=\"ToastGeneric\">")
             .Append("<text>").Append(Escape(request.Title)).Append("</text>")
             .Append("<text>").Append(Escape(request.Body)).Append("</text>")
             .Append("</binding></visual>");

        if (request.Actions.Count > 0)
        {
            toast.Append("<actions>");

            foreach (var action in request.Actions)
            {
                var (type, argument) = action.Kind switch
                {
                    // A URL crua como argumento: o shell a abre sem intermediário.
                    ToastActionKind.Join => ("protocol", action.Argument),

                    // "dismiss" é palavra reservada do shell, não texto nosso.
                    ToastActionKind.Dismiss => ("system", "dismiss"),

                    _ => ("foreground", $"ack|{action.Argument}"),
                };

                toast.Append("<action content=\"").Append(Escape(action.Label))
                     .Append("\" activationType=\"").Append(type)
                     .Append("\" arguments=\"").Append(Escape(argument))
                     .Append("\"/>");
            }

            toast.Append("</actions>");
        }

        toast.Append("</toast>");
        return toast.ToString();
    }

    /// <summary>
    /// Escapa para <b>atributo</b>, e não só para texto. <c>SecurityElement.Escape</c> não toca nas
    /// aspas duplas, e título de reunião com aspas é rotina — sem isto o XML seria recusado e o
    /// aviso simplesmente não sairia.
    /// </summary>
    private static string Escape(string value) =>
        SecurityElement.Escape(value)?.Replace("\"", "&quot;") ?? string.Empty;
}
