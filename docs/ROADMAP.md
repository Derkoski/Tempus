# Roadmap

Regra que governa este roadmap: **cada fase termina em algo que roda.** Nunca uma fase que
entrega "a camada de serviços" sem nada visível. Isso mantém o risco descoberto cedo e dá
ponto de parada limpo em qualquer sessão.

As fases 1 e 2 são **independentes** — se a TI da TOTVS demorar com o OAuth client (D-003),
a Fase 2 anda inteira contra dados falsos.

---

## Fase 0 — Fundação ✅ *concluída em 2026-08-07*

- [x] `git init` + `.gitignore` com proteção de segredos
- [x] `CLAUDE.md` — briefing de sessão
- [x] `docs/SPEC.md` — spec de produto e critérios de aceite
- [x] `docs/SEVERITY.md` — modelo de severidade e cor
- [x] `docs/DECISIONS.md` — D-001 a D-005
- [x] `docs/ROADMAP.md`

---

## Fase 1 — Spike de OAuth e dados reais

**Entrega:** um app de console que autentica e imprime a agenda de hoje e as tarefas abertas,
lidas da conta real.

### 1a — Teste go/no-go ✅ *passou em 2026-08-11*

O único risco que podia matar o projeto inteiro (D-003).

- [x] Projeto GCP — **numa conta pessoal `@gmail.com`**, não na TOTVS: a conta corporativa não tem
      `resourcemanager.projects.create`, e conta Workspace fica presa à organização dela
- [x] OAuth client Desktop, consent screen External + Testing, **e-mail da TOTVS como test user**
- [x] Consent com a conta corporativa: **o admin permitiu** — o *App access control* não barra
- [x] Chamadas reais de Calendar e Tasks retornando dados
- [x] Token gravado cifrado com DPAPI (cabeçalho de blob conferido, não é JSON em claro)

O fallback de iCal fica arquivado, sem necessidade de uso.

### 1b — Integração *(código pronto, esperando credenciais)*

- [x] Fluxo OAuth com loopback local, token persistido com **DPAPI** (`DpapiDataStore`) — o
      `FileDataStore` padrão do Google grava JSON em claro, o que a regra 5 não permite
- [x] `login_hint` na URL de consent, via `GoogleAuthorizationCodeFlow` estendido
- [x] Separação entre restauração **silenciosa** (startup, nunca abre navegador) e autorização
      **interativa** (só a partir de clique) — com D-003 permanente, "token expirado" é rotina
      semanal e abrir navegador sozinho seria hostil
- [x] `Google.Apis.Calendar.v3` — eventos de hoje, com filtro de `cancelled`, `transparent`,
      `declined`, `outOfOffice`/`focusTime`, dia inteiro e extração do link de Meet
- [x] `Google.Apis.Tasks.v1` — tarefas abertas de todas as listas, com vencimento lido em **UTC**
      (converter para local jogaria o dia para trás em fuso negativo)
- [x] Escrita: concluir e criar tarefa (antecipado da Fase 4 — a UI já existia e sem isso mentiria)
- [x] Estado `Offline` por `NeedsAuth`, `NotConfigured` ou sync > 10 min, com contadores em `—`
- [x] Re-consent sem reiniciar o app
- [x] **Credenciais do GCP** — em uso; a barra roda com dados reais da conta

**Desvio consciente do plano original:** sem `syncToken`. Ele serve para sincronizar um calendário
inteiro e é **incompatível com `timeMin`/`timeMax`** — e o que a barra precisa é só "os eventos de
hoje". Uma consulta com janela de um dia é uma requisição por minuto, folgada na quota, e dispensa
o estado extra de invalidação por HTTP 410.

---

## Fase 2 — Spike visual da barra

**Entrega:** a barra visível sobre a taskbar com dados falsos, passando o critério de aceite 1
do `SPEC.md`.

- [x] Descartar o scaffold `Microsoft.NET.Sdk.Web`; criar projeto WPF `net8.0-windows`
- [x] Interface `IShellSurface` + `FloatingBarSurface` (D-002)
- [x] Janela sem borda: `WS_EX_TOOLWINDOW`, `WS_EX_NOACTIVATE`, topmost, fora da taskbar
      — os três estilos verificados por `GetWindowLongPtr`
- [x] Posicionamento via rect do `Shell_TrayWnd`, com teto rígido contra a bandeja
- [x] Largura de 12 slots (480px): 5 slots deixavam ~64px de texto, pequeno demais
- [x] Reafirmação de ordem Z — ver "Achado da Fase 2" em D-002. O heartbeat de 1s sozinho deixava
      a barra ~500ms atrás da taskbar a cada troca de janela; passou a ser orientada a evento
      (`EVENT_SYSTEM_FOREGROUND`) com o heartbeat como rede de segurança (D-014)
- [x] Robustez escrita: `TaskbarCreated`, `WM_DPICHANGED`, `WM_DISPLAYCHANGE`, lock/unlock
- [x] Esconder em tela cheia via `SHQueryUserNotificationState`
- [x] Layout: relógio · motivo · contador de tarefas · contador de e-mail
- [x] Consumo em repouso: 0,13% de um núcleo (critério de aceite 10)
- [x] Painéis S2 e S3 construídos, com dados falsos e o laço painel→barra
- [x] **Verificar que os painéis abrem** — feito em 2026-08-17. S3 aberto por clique sintético no
      menu de contexto e capturado em tela; S2 exercitado pelo usuário. Ambos renderizam dados
      reais. A tentativa anterior falhou porque a barra ainda estava atrás da taskbar e o clique ia
      para o explorer — era o bug da ordem Z (D-014), não os painéis.
- [ ] Exercitar `TaskbarCreated` com restart real do explorer (o handler existe, nunca rodou)
- [x] Verificar propriedade dos pixels, não só de retângulo — feito por sonda de ordem Z contra
      `Shell_TrayWnd` (D-014), que é melhor que o `WindowFromPoint` previsto: compara a ordem
      inteira em vez de um pixel só, sem falso negativo em sobreposição parcial

**Risco que esta fase mata:** o posicionamento sobre a taskbar ser inviável na prática. Segue de
pé, mas o achado da ordem Z mostrou que a fragilidade prevista em D-002 é real e se manifesta em
minutos de uso, não em updates do Windows. Se reaparecer sob outra forma, `TraySurface` entra sem
tocar em lógica de domínio.

---

## Pausas de descanso ✅ *pedida e entregue em 2026-08-17*

**Entrega:** 15 minutos de descanso na manhã e na tarde, encaixados na janela livre mais próxima
do meio de cada período.

Fora do numeramento das fases porque não estava no plano: veio do uso. Não depende da Fase 3 e não
a bloqueia.

- [x] `BreakPlanner` — função pura de (agenda, expediente, hora) → até duas janelas de 15 min.
      Varre a grade de 5 min e escolhe a livre **mais próxima do meio** do período, não a primeira
      que couber
- [x] Recalculada a cada sync: se marcarem reunião em cima, a pausa **se move** sozinha
- [x] Slot no painel S3 o dia todo; na barra, texto ambiente só durante a janela
- [x] **Três níveis de controle**, decididos com o usuário:
      1. *Instalação:* nasce **desabilitada** (`appsettings` → `Breaks.Enabled`). Só quem quer liga.
      2. *Ligada:* a folga existe **todo dia**, sem precisar pedir.
      3. *Por dia:* "Hoje não quero pausa" no menu da barra, persistido por data em `%APPDATA%`
- [x] Sem cor nova: itálico no verde de "Livre", sem fundo de serviço (regra 1). Ver D-019
- [x] Caso sem solução: período sem 15 min livres não inventa pausa nem alarma
- [ ] **Texto ambiente na barra não verificado visualmente** — só aparece durante os 15 min e com
      o humor em `Free`; a verificação caiu no almoço

**Decisão de escopo:** vive só no Tempus, sem virar evento no Google Calendar. Evita subir de
`CalendarReadonly` para escopo de escrita numa conta corporativa, e mantém a pausa móvel — um
evento real ficaria parado no horário errado. Promover a evento real fica para *se* o problema
virar "colegas marcam por cima"; o cálculo da janela é o mesmo nos dois casos.

---

## Tela de configuração ✅ *pedida e entregue em 2026-08-17*

**Entrega:** e-mail, expediente e pausas configuráveis por UI, com o dado pessoal fora do repo.

- [x] Duas camadas: `appsettings.json` é padrão de fábrica, `%APPDATA%\Tempus\settings.json` são as
      escolhas do usuário
- [x] E-mail **removido do repositório** — `LoginHint` vazio no padrão de fábrica
- [x] Tela com Conta, Expediente e Pausas; ajuste fino continua no JSON
- [x] Primeira execução exige o e-mail, com o botão rotulado "Sair" em vez de "Cancelar"
- [x] `--demo` escapa da exigência: existe para rodar sem conta
- [x] Item "Configurações…" no menu da barra, com mudanças valendo sem reiniciar
- [x] Tema escuro nos controles e na barra de título

Ver D-020, incluindo os três achados da verificação: diálogo herdando o não-foco da barra,
controles do WPF ignorando o tema, e propriedade derivada vazando para o JSON.

---

## Fase 3 — Máquina de estados, cores e toasts

**Entrega:** a barra reage aos dados reais da Fase 1 com as cores e avisos corretos.

- [x] Questões abertas fechadas — nenhuma pendência bloqueando esta fase
- [ ] Estado `Offline` que precede e anula a escala (`SEVERITY.md` §0), com contadores em `—`
- [ ] Identidade de ocorrência `(eventId, início, fim)` e supressão por ocorrência (D-010)
- [ ] `MeetingAmbiguous` + seletor de evento ativo, com padrão determinístico (D-009)
- [ ] Sinais como funções puras, testáveis sem UI nem rede
- [ ] Arbitragem com desempate por categoria (`SEVERITY.md` §4)
- [ ] Histerese de 20s na descida, subida imediata (I4, I5)
- [ ] **Reconhecimento por clique**: suprime a ocorrência, volta a `Calm` imediatamente (D-006)
- [ ] **Escalada do nível 3**: vermelho sólido → pisca âmbar↔vermelho após 5 min, período ~1,2s,
      sólido se as animações do sistema estiverem desligadas
- [ ] Persistência da supressão de `DayEnded` até a virada do dia
- [ ] Testes das invariantes I1–I8
- [ ] Toasts com AUMID registrado, com deduplicação e supressão em apresentação
- [x] Feriados computados localmente, com Páscoa por Meeus/Jones/Butcher (D-008) — `BrazilianHolidays`,
      cobrindo nacionais, Paraná e Pato Branco, mais emendas por lista manual
- [x] Fronteiras do dia configuráveis (D-007) — `WorkDayOptions` (08:00/12:00/13:00/17:00) e
      `BoundaryStatus` com gradiente verde→âmbar→vermelho na janela final
- [ ] `DayEnded.CountMode` (D-007) — **não implementado**; é o que resta do item acima

**Marco:** ao fim desta fase o produto já entrega o valor central. Fases 4 e 5 são
complementos.

---

## Fase 4 — Tasks bidirecional

**Entrega:** criar/concluir/editar tarefa no painel reflete no Google Tasks e vice-versa.

- [ ] Escritas: criar, concluir, editar título e vencimento
- [ ] Fila local de escritas offline, reproduzida ao voltar a conexão
- [ ] Resolução de conflito: última escrita vence, Google ganha em empate
- [ ] Atualização otimista da UI com reversão em caso de falha
- [ ] Critério de aceite 6 do `SPEC.md` verificado nos dois sentidos

---

## Fase 5 — Gmail

**Entrega:** contador de não-lidos real.

*Entregue fora de ordem, junto com a Fase 1 — o escopo saía no mesmo consent e o contador já
existia na barra sem dado por trás.*

- [x] `gmail.readonly` no consent (restricted, permitido em Testing mode para test users)
- [x] Contagem de não-lidos, polling de 60s, sem tocar em conteúdo
- [x] Contador sempre neutro, nunca colorindo a barra (`SEVERITY.md` §2.4)
- [ ] Critério de aceite 7 do `SPEC.md` verificado formalmente

**Desvio consciente:** conta **conversas** via `users.threads.list` com query, e não
`messagesUnread` do rótulo `UNREAD` como este roadmap previa. O caminho planejado errava por dois
motivos — contava mensagens em vez de conversas (uma thread com 5 respostas virava 5, e o Gmail
mostra 1) e o rótulo `INBOX` abrange Promoções, Social e Atualizações. Só identificadores de
conversa trafegam; nenhum assunto, remetente ou corpo é lido.

*Fase encolhida por D-006: a detecção de microfone que morava aqui saiu do escopo, e
`MeetingRanIntoNext` passou a ser baseado apenas em cronograma, entregue na Fase 3.*

---

## Depois (não comprometido)

- Reposicionar/redimensionar a barra por arrastar, com posição persistida
- Ações rápidas no toast ("entrar na call", "concluir tarefa")
- Registro de foco: quanto tempo em reunião vs. livre por dia
- Feriados municipais/estaduais e emendas da TOTVS por lista manual (`SEVERITY.md` §7)
- Reconsiderar Chat se o Google publicar contagem de não-lidos (D-004)
- Reconsiderar detecção de microfone como refinamento dos sinais existentes, se o clique se
  mostrar insuficiente na prática (D-006 é revogável sem redesenho)
