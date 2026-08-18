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
- [x] Verificar propriedade dos pixels, não só de retângulo — sonda de ordem Z contra
      `Shell_TrayWnd`, melhor que o `WindowFromPoint` previsto: compara a ordem inteira em vez de
      um pixel só, sem falso negativo em sobreposição parcial.
      **A sonda só passou a existir de fato em 2026-08-18 (D-021)** — até então o D-014 a descrevia
      e o código reafirmava o topo *sem medir*, o que empurrava os próprios menus e dicas da barra
      para trás dela a cada rodada

**Risco que esta fase mata:** o posicionamento sobre a taskbar ser inviável na prática. Segue de
pé, mas o achado da ordem Z mostrou que a fragilidade prevista em D-002 é real e se manifesta em
minutos de uso, não em updates do Windows. Se reaparecer sob outra forma, `TraySurface` entra sem
tocar em lógica de domínio.

---

## Pausas de descanso ✅ *pedida em 2026-08-17, amadurecida em 2026-08-18*

**Entrega:** 15 minutos de descanso na manhã e na tarde, sugeridos na janela livre mais próxima do
meio de cada período.

Fora do numeramento das fases porque não estava no plano: veio do uso. Não depende da Fase 3 e não
a bloqueia.

**O princípio, formulado pelo usuário:** *"a folga é uma sugestão, pode ou não acontecer naquele
horário ou em um horário inesperado."* O Tempus **propõe**; quem decide é quem descansa. Tudo
abaixo decorre disso.

- [x] `BreakPlanner` — função pura de (agenda, expediente) → até duas janelas. Varre a grade de
      5 min e escolhe a livre **mais próxima do meio** do período, não a primeira que couber
- [x] Recalculada a cada sync a partir da agenda: uma call em cima da folga a empurra para a
      primeira janela livre depois dela. Verificado em uso — treinamento de 14:00–15:00 moveu a
      pausa da tarde para 15:00–15:15
- [x] **Do período, não do instante** (D-023): fica no mesmo horário o dia todo e só muda se a
      agenda mudar. A primeira versão reagendava para frente quando a pausa vencia, e perseguia o
      usuário o dia inteiro — *"sempre tô com pausa pra fazer"*
- [x] Slot próprio na barra, que **colapsa** com a funcionalidade desligada; e linha no painel S3
- [x] **Quatro gestos**, porque a decisão é do usuário:
      | Gesto | Diz | Onde |
      |-------|-----|------|
      | Tirar pausa agora | "estou descansando" | menu |
      | Adiar 30 min | "agora não, mais tarde" | menu |
      | Tirei essa | "já descansei" | clique no slot |
      | Hoje não quero | "hoje não" | menu |
- [x] **Três níveis de controle**, decididos com o usuário:
      1. *Instalação:* nasce **desabilitada** (`appsettings` → `Breaks.Enabled`). Só quem quer liga.
      2. *Ligada:* a folga existe **todo dia**, sem precisar pedir.
      3. *Por dia:* dispensável, persistida por data em `%APPDATA%\Tempus\break-state.json`
- [x] Sem cor nova: itálico no verde de "Livre", sem fundo de serviço (regra 1). Ver D-019
- [x] Caso sem solução: período sem 15 min livres não inventa pausa nem alarma
- [x] **O app nunca infere se a folga foi tirada** (D-006). Pausa perdida apaga, nunca cobra

**Decisão de escopo:** vive só no Tempus, sem virar evento no Google Calendar. Evita subir de
`CalendarReadonly` para escopo de escrita numa conta corporativa, e mantém a pausa móvel — um
evento real ficaria parado no horário errado. Promover a evento real fica para *se* o problema
virar "colegas marcam por cima"; o cálculo da janela é o mesmo nos dois casos.

**Três defeitos que só o uso revelou**, todos corrigidos: a pausa que reagendava sozinha, o clique
de "tirei essa" sem volta, e o adiar que *antecipava* (piso contado de `agora + 30` em vez de a
partir da própria pausa). São todos lógica pura — o tipo que um teste de invariante pega em
segundos.

---

## Barra em três slots ✅ *2026-08-18*

**Entrega:** a barra deixa de ser uma frase e vira três perguntas com respostas próprias.

- [x] **Status** (`[Livre]`, `[Ocupado]`, `[Encerrando]`…) em bloco com fundo próprio, largura
      própria, nunca cede. Fundo neutro nos humores calmos, **aceso** nos que escalam — a escalada
      continua sendo forma, não só cor (D-012, D-023)
- [x] **Cronograma**: o compromisso de hoje ou, não havendo, o de amanhã. É quem trunca quando
      aperta. Um por vez — dois lado a lado faziam a barra parecer ter duas agendas (D-022)
- [x] **Pausa**: colapsa por completo com a funcionalidade desligada
- [x] Ordem da frase decidida pelo **custo de truncar**: `rótulo · quando · título`. O horário tem
      tamanho fixo e é o que o usuário pediu para nunca perder; o título é longo e descartável
- [x] Perto usa contagem, longe usa relógio, fronteira em uma hora. A dica traz a outra metade
- [x] Cada slot tem seu clique: o bloco diz "eu vi", o texto age sobre o compromisso

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

- [x] Questões abertas fechadas — Q-01 aberto e fechado em 2026-08-18
- [ ] Estado `Offline` que precede e anula a escala (`SEVERITY.md` §0), com contadores em `—`
- [x] **Identidade de ocorrência e supressão** (D-010) — `(sinal, eventId, início, fim)`. O sinal
      entrou além do previsto no §7: sem ele, reconhecer `Encerrando` calaria o `Estourou`
      seguinte, que é um fato novo e pior. Persistida por dia em `acknowledged.json`
- [ ] `MeetingAmbiguous` + seletor de evento ativo, com padrão determinístico (D-009)
- [x] **Sinais como funções puras** — `TimeStatusResolver`, `BreakPlanner`, `WorkDayResolver` e
      `Lookahead` são funções de (dados, hora, opções) sem I/O. Falta só a arbitragem, que ainda
      não existe
- [ ] Arbitragem com desempate por categoria (`SEVERITY.md` §4)
- [ ] Histerese de 20s na descida, subida imediata (I4, I5)
- [x] **Reconhecimento por clique**: suprime a ocorrência, volta ao estado calmo imediatamente
      (D-006, I3). Fecha o Q-01 — ver `SEVERITY.md` §10. Verificado ao vivo num estouro real
- [x] **Escalada do nível 3** (D-025): vermelho sólido → pisca âmbar↔vermelho após 5 min, período
      1,2s, sólido se as animações do sistema estiverem desligadas. **Sem timer** — o alarme começa
      no fim marcado da reunião, então a escalada é uma subtração e sobrevive a restart de graça.
      ⚠️ *Nesta máquina as animações do Windows estão desligadas, então na prática ele fica sólido*
- [ ] Persistência da supressão de `DayEnded` até a virada do dia — o mecanismo já existe
      (`AcknowledgementStore`, escopado por data); falta o sinal `DayEnded` usá-lo
- [x] **Projeto de teste** — `Tempus.Tests` (xUnit), `InternalsVisibleTo` em vez de extrair
      `Tempus.Domain`. 50 testes, 62 ms. `dotnet test Tempus.Tests/Tempus.Tests.csproj`
- [x] **Invariantes testáveis hoje**: I2 (severidade colorida tem motivo), I3 (todo alarme é
      reconhecível e reconhecer resolve na hora), I6 (contador sobrevive à severidade), I7
      (`Offline` não mostra número nem avalia sinal)
- [x] **I8** — coberta junto com a escalada (D-025)
- [ ] **I1, I4, I5** — dependem de arbitragem e histerese, que ainda não existem. Estão no código
      como testes `Skip` **com o motivo**: um teste ausente some da vista, um teste pulado cobra
- [ ] Toasts com AUMID registrado, com deduplicação e supressão em apresentação
- [x] Feriados computados localmente, com Páscoa por Meeus/Jones/Butcher (D-008) — `BrazilianHolidays`,
      cobrindo nacionais, Paraná e Pato Branco, mais emendas por lista manual
- [x] Fronteiras do dia configuráveis (D-007) — `WorkDayOptions` (08:00/12:00/13:00/17:00) e
      `BoundaryStatus` com gradiente verde→âmbar→vermelho na janela final
- [ ] `DayEnded.CountMode` (D-007) — **não implementado**; é o que resta do item acima

**Marco:** ao fim desta fase o produto já entrega o valor central. Fases 4 e 5 são
complementos.

### ⚠️ Os testes deixaram de ser opcionais

Em 2026-08-18, **quatro defeitos escaparam para o uso** em um único dia: a pausa que reagendava
sozinha, o clique de "tirei essa" sem volta, o alvo estreito do ✕ de excluir, e o adiar que
antecipava. Dois foram pegos verificando; **dois só apareceram porque o usuário usou.**

Todos são lógica pura, sem UI e sem rede — exatamente o que os testes das invariantes cobririam em
segundos. `dotnet test` ainda é "a definir" no `CLAUDE.md`, e não há projeto de teste no repo.

A prioridade dentro da Fase 3 subiu: os testes vêm **antes** da escalada do nível 3 e dos toasts,
não depois.

**Feito em 2026-08-18.** `Tempus.Tests` com xUnit, 50 testes em 62 ms. Estrutura decidida com o
usuário: **manter o domínio dentro do projeto WPF** com `InternalsVisibleTo`, em vez de extrair
uma biblioteca `Tempus.Domain`. Mais rápido e sem reestruturação; em troca, a pureza dos sinais
(regra 8) continua sendo disciplina e não imposição do compilador.

Metade da suíte são **regressões dos defeitos que chegaram ao usuário**: a pausa que perseguia, a
call em cima da folga, o adiar que antecipava, o clique que entrava numa call de daqui a horas, os
dois compromissos lado a lado, e o reconhecimento de "Encerrando" que não pode calar "Estourou".

*Ressalva honesta: escritos depois da correção, esses testes não provam que teriam pego o defeito
na época. Eles impedem a volta, que é o que importa daqui para frente.*

---

## Fase 4 — Tasks bidirecional

**Entrega:** criar/concluir/editar tarefa no painel reflete no Google Tasks e vice-versa.

*Parcialmente antecipada: criar, concluir e excluir já existem porque a UI as pedia e sem elas
mentiria.*

- [x] Criar e concluir tarefa (antecipado na Fase 1)
- [x] **Excluir tarefa** (D-024) — ✕ no hover, com confirmação de dois cliques na própria linha.
      Excluir no Google Tasks não tem lixeira
- [ ] Editar título e vencimento
- [ ] Fila local de escritas offline, reproduzida ao voltar a conexão
- [ ] Resolução de conflito: última escrita vence, Google ganha em empate
- [ ] Atualização otimista da UI com reversão em caso de falha
- [ ] Critério de aceite 6 do `SPEC.md` verificado nos dois sentidos

**Regra aprendida em D-024:** verificação de escrita usa o **modo demo**, nunca a conta real. Um
clique de teste 15 pixels fora do alvo concluiu uma tarefa de verdade do usuário. `FakeStateSource`
ganhou `DeleteTask` no mesmo commit.

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

- ~~**Widget de Android**~~ — **avaliado e recusado em 2026-08-18.** Não por custo: a toolchain
  inteira daria ~3 GB contra 46 GB livres, e o spike caberia numa sessão.

  Recusado porque **a premissa do produto não atravessa.** A barra funciona por ser *ambiente*:
  ela nunca interrompe, porque o usuário já está olhando para a tela. É disso que decorre o modelo
  inteiro do `SEVERITY.md` — cor como canal principal, toast como exceção (§5), cor como recurso
  escasso (regra 1). Nas palavras do usuário: *"o celular está no bolso e o Tempus no Windows, eu
  estou olhando ele direto."*

  Num celular não há canal ambiente: tudo que precisa alcançar você vira interrupção. A escassez
  muda de lugar — deixa de ser a cor e passa a ser a interrupção — e as invariantes I1–I8, que
  falam de cor, escalada visual e histerese, ficam sem correspondente. **Não seria um port, seria
  outro produto usando os mesmos dados.**

  Dois limites técnicos, secundários mas reais: o re-consent de 7 dias (D-003) ficaria visível na
  tela inicial toda semana em vez de ser rotina invisível; e `WorkManager` tem intervalo mínimo de
  15 min, o que impede o "faltam 5 min" que é o valor central.

  E a pergunta que um celular responderia — "vai começar uma reunião e não estou na frente do PC" —
  o Google Calendar já responde nativamente.

  **O que mudaria a decisão:** o usuário passar a trabalhar longe da tela com frequência, ou
  aparecer uma necessidade de notificação que o Calendar não cubra. Aí o desenho começa da pergunta
  certa, não de portar este.
- Reposicionar/redimensionar a barra por arrastar, com posição persistida
- Ações rápidas no toast ("entrar na call", "concluir tarefa")
- Registro de foco: quanto tempo em reunião vs. livre por dia
- Feriados municipais/estaduais e emendas da TOTVS por lista manual (`SEVERITY.md` §7)
- Reconsiderar Chat se o Google publicar contagem de não-lidos (D-004)
- Reconsiderar detecção de microfone como refinamento dos sinais existentes, se o clique se
  mostrar insuficiente na prática (D-006 é revogável sem redesenho)
