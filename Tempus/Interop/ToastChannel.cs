using System.Diagnostics;
using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Tempus.Interop;

/// <summary>
/// A saída de toast do Windows. Não decide nada: quem decide é <c>ToastPolicy</c>, que é pura e
/// testável. Aqui só se monta o XML e se entrega ao shell.
/// <para>
/// <b>Sem botões e sem tratador de clique.</b> Um app não empacotado só recebe ativação de toast
/// registrando um servidor COM no registro do Windows, e isso é bastante máquina para um app de
/// um usuário só — pior, é máquina que precisa ser desregistrada na desinstalação. O toast aqui é
/// <b>anúncio</b>; o gesto continua sendo o clique na barra, que já existe e já funciona (regra 2).
/// O corpo do texto diz isso em voz alta, para o usuário não ficar procurando o botão.
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
    /// Quanto tempo o aviso sobrevive na Central de Ações. A barra é a superfície persistente —
    /// enquanto o vermelho não for reconhecido ele continua lá, colorido. A cópia na Central é
    /// conveniência para quem estava longe da tela, e não precisa atravessar o dia.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    private ToastNotifier? _notifier;
    private int _failures;

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

    /// <param name="tag">Identidade na Central de Ações: mesma etiqueta substitui o aviso anterior.</param>
    public void Show(string title, string body, string tag)
    {
        if (_notifier is null || !IsAvailable) return;

        try
        {
            var xml = new XmlDocument();

            // ToastGeneric é o único template que ainda vale no Windows 10+; os antigos
            // (ToastText02 e parentes) continuam funcionando mas são convertidos por baixo.
            xml.LoadXml($"""
                <toast>
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{SecurityElement.Escape(title)}</text>
                      <text>{SecurityElement.Escape(body)}</text>
                    </binding>
                  </visual>
                </toast>
                """);

            _notifier.Show(new ToastNotification(xml)
            {
                Tag = tag,
                Group = "tempus",
                ExpirationTime = DateTimeOffset.Now + Lifetime,
            });

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
    /// Retira o aviso da Central de Ações. Chamado quando o usuário reconhece na barra: manter lá
    /// um vermelho que já foi resolvido é mostrar dado velho com cara de atual, que é justamente o
    /// que a regra 10 proíbe. Some da Central pelo mesmo gesto que apaga a cor.
    /// </summary>
    public void Withdraw(string tag)
    {
        if (_notifier is null || !IsAvailable) return;

        try
        {
            ToastNotificationManager.History.Remove(tag, "tempus", AppIdentity.Aumid);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Toast não retirado da Central: {e.Message}");
        }
    }
}
