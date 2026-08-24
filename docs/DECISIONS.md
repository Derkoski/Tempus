# Decisões de Arquitetura

Formato: contexto → decisão → consequências. Uma decisão revogada não é apagada; é marcada
como *Substituída por D-NNN*, para que o histórico do raciocínio sobreviva.

---

## D-001 — WPF sobre .NET 8, app não-empacotado

**Status:** Aceita · 2026-08-07

**Contexto.** O app precisa de: janela sem borda always-on-top posicionada com precisão em
coordenadas de tela, interop Win32 pesado (taskbar, DPI, notificações, sessões de áudio),
DPI per-monitor v2, e renderização de conteúdo dinâmico. O desenvolvedor tem fluência em C#.

**Decisão.** WPF sobre .NET 8 (LTS, já instalado: SDK 8.0.303), `net8.0-windows`, não-empacotado.

Alternativas descartadas:

| Opção | Motivo da recusa |
|-------|------------------|
| WinUI 3 / Windows App SDK | Controle de janela menos flexível, always-on-top e no-activate são fricção, MSIX praticamente obrigatório. Errado para utilitário de shell. |
| WinForms | Ótimo para tray, ruim para os painéis (S2/S3). Fica como fallback se WPF criar problema de composição sobre a taskbar. |
| Avalonia | Adiciona risco de abstração sobre Win32 sem entregar valor — não há requisito cross-platform. |
| Electron / web | Consumo de memória e latência de startup inaceitáveis para algo sempre aberto. |

**Consequências.** O scaffold atual (`Microsoft.NET.Sdk.Web` com minimal API "Hello World")
é descartado na Fase 2 — foi criado por engano e não representa nenhuma decisão.
Notificações toast em app não-empacotado exigem AUMID registrado via atalho no Menu Iniciar;
custo pontual, resolvido uma vez.

---

## D-002 — Barra flutuante sobre a taskbar, com abstração de superfície

**Status:** Aceita · 2026-08-07

**Contexto.** O pedido original era ocupar espaço *dentro* da taskbar. Isso era possível via
**Deskband** (API por trás de BatteryBar, NetSpeedMonitor, EverythingToolbar). A taskbar XAML
do Windows 11 **não suporta deskband**, e em builds recentes a API foi removida, não apenas
desativada. Máquina alvo: build 26200. Portanto: impossível pelo caminho suportado.

Opções reais avaliadas: múltiplos ícones de tray (robusto, pequeno), janela flutuante
posicionada sobre a taskbar (visual desejado, frágil), AppBar via `SHAppBarMessage` (robusto,
mas rouba espaço de tela), overlay no próprio botão da taskbar (1 número só), widget do Win11
(não é sempre-visível).

**Decisão.** Barra flutuante always-on-top posicionada sobre a taskbar, como escolhido pelo
usuário com o risco de fragilidade explicitamente à vista.

**Mitigação obrigatória:** toda a renderização fica atrás de uma interface `IShellSurface`,
com `FloatingBarSurface` como implementação primária e `TraySurface` como fallback previsto.
Nenhuma lógica de domínio conhece a superfície. Se um update do Windows quebrar o
posicionamento, a troca é de implementação, não de arquitetura.

**Restrições técnicas de implementação:**

- **Nunca** usar `SetParent` para reparentar dentro de `Shell_TrayWnd`. É o hack que quebra
  de forma severa em restart do explorer, mudança de DPI e updates do Windows.
- Posicionar por coordenada: localizar `Shell_TrayWnd` via `FindWindow` + `GetWindowRect`, e
  colocar a janela numa região vazia conhecida.
- Estilos de janela: `WS_EX_TOOLWINDOW` (fora do Alt+Tab) + `WS_EX_NOACTIVATE` (não rouba
  foco) + `Topmost` + `ShowInTaskbar=false`.
- Reagir a: mensagem broadcast `TaskbarCreated` (explorer reiniciou → reposicionar),
  `WM_DPICHANGED`, `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE`, e bloqueio/desbloqueio de sessão.
- Ler estado de auto-hide via `ABM_GETSTATE` e acompanhar quando a taskbar se esconde.
- Esconder em tela cheia via `SHQueryUserNotificationState`.
- Sem timer de reposicionamento por polling em loop apertado — reagir a eventos, e no máximo
  uma verificação de sanidade a cada poucos segundos.

**Consequências.** A Fase 2 gasta esforço real com robustez de posicionamento em vez de
features. Aceito conscientemente em troca do visual pretendido. O critério de aceite 1 do
`SPEC.md` existe para provar essa robustez.

### Achado da Fase 2: perder a ordem Z é o pior modo de falha

Medido em 2026-08-07, com a barra rodando: `WindowFromPoint` sobre os pixels da barra retornava
`Shell_TrayWnd` do explorer, não a barra. Ou seja, **a barra estava atrás da taskbar**.

A taskbar também é `HWND_TOPMOST`, e o explorer a reposiciona por conta própria, o que empurra a
barra para trás dela. Não existe evento para isso. Pior: quando acontece, a barra não só fica
invisível — ela fica **surda a cliques**, porque os pixels passam a pertencer ao explorer. Como o
clique é o gesto central do produto (D-006), isso quebra tudo de uma vez.

Causa no código: `Reposition()` só chamava `SetWindowPos` quando o retângulo mudava, então a
ordem Z nunca era reafirmada depois da primeira colocação.

Correção: reafirmar `HWND_TOPMOST` a cada 1s (uma chamada de `SetWindowPos` com `SWP_NOMOVE |
SWP_NOSIZE`), independente de o retângulo ter mudado. Frequência maior que a da verificação de
posição (2s) justamente porque a consequência é mais grave.

**Lição para a superfície:** verificar posição não basta; é preciso verificar *propriedade dos
pixels*. `WindowFromPoint` sobre o retângulo da própria barra é a única checagem que não mente —
e é a que deve virar teste automatizado, não a comparação de retângulos.

---

## D-003 — OAuth em modo Testing, permanentemente

**Status:** Aceita · 2026-08-07 · **permanente, não há caminho alternativo**

**Contexto.** Acesso às APIs exige um OAuth client. Caminhos possíveis:

| Caminho | Verificação | Vida do refresh token | Restricted scopes | Viável? |
|---------|-------------|----------------------|-------------------|---------|
| External + Testing | nenhuma | **7 dias** | permitidos para test users | ✅ |
| External + Production | exigida (CASA, paga e lenta para restricted) | indefinida | só após verificação | ❌ custo desproporcional para uso pessoal |
| Internal na org GCP da TOTVS | nenhuma | indefinida | permitidos | ❌ **descartado** |

O caminho *Internal* seria tecnicamente ideal, mas foi descartado pelo usuário: a TOTVS tem
~90 mil funcionários e este é um utilitário de uso estritamente pessoal. Pedir provisionamento
de OAuth client na org corporativa para uma ferramenta de uma pessoa tem chance ~0 de aprovação,
e o pedido em si não se justifica. **Não é uma pendência — é um caminho fechado.**

**Decisão.** **External + Testing**, permanentemente, com a própria conta como test user e
re-consent a cada 7 dias como parte do funcionamento normal do app.

**Consequências.**

- O estado `Offline` do `SEVERITY.md` §0 deixa de ser tratamento de erro e passa a ser **um
  estado normal semanal do produto**. Entra na Fase 1, não depois.
- O fluxo de re-consent (`SEVERITY.md` §6) precisa custar dois cliques: barra cinza → clique →
  navegador (já logado, `login_hint` pré-seleciona a conta) → *Permitir* → volta sem reiniciar.
- **Não é possível reaproveitar a sessão do Chrome.** Tokens OAuth são vinculados ao
  `client_id`, e os cookies de sessão do Google no Chrome não são acessíveis a apps de fora — por
  design, já que o contrário seria um vetor de roubo de credencial. O que *é* aproveitado: o
  navegador já está autenticado, então o consent não pede senha nem 2FA.
- Restricted scopes (`gmail.readonly`) **continuam funcionando** em Testing mode para test users.
  Nada de escopo se perde por essa decisão.
- `client_secret.json` e o token **nunca** entram no repo (já no `.gitignore`). Token em disco
  protegido com DPAPI vinculado ao usuário.

**Risco residual — resolvido em 2026-08-11: a TOTVS NÃO bloqueia.**

O medo era o admin do Workspace recusar apps de terceiros não configurados
(*API Controls → App access control*), o que não teria contorno pelo caminho OAuth. Testado com a
conta corporativa real: o consent foi concedido, o refresh token emitido, e Calendar e Tasks
responderam com dados. **O caminho está aberto.**

Descoberta paralela, que muda o procedimento de setup: a conta TOTVS **não consegue criar projeto
no Google Cloud** — falta `resourcemanager.projects.create`, revogado do domínio, e toda conta
Workspace fica presa à organização dela, então nem "Sem organização" funciona. A saída é criar o
projeto GCP numa **conta pessoal `@gmail.com`** e adicionar o e-mail da TOTVS como *usuário de
teste*. Um OAuth client é só um identificador; ele não precisa pertencer à organização cujos dados
serão lidos. Quem autoriza é o usuário, na tela de consent.

**Plano B, agora arquivado:** se o OAuth tivesse sido bloqueado, restaria o endereço secreto em
formato iCal do Google Calendar (URL privada por calendário, HTTP puro, sem OAuth), que salvaria a
metade de agenda e perderia Tasks e Gmail. Não foi necessário.

---

## D-006 — Sem detecção de presença em call; reconhecimento por clique

**Status:** Aceita · 2026-08-07

**Contexto.** O desenho inicial usava o estado do microfone (`IAudioSessionManager2`) para
distinguir "você está na call" de "você já saiu", o que alimentava `MeetingStartedNotJoined`,
`MeetingOverran` e `MeetingOverranIntoNext`. Questionado, o usuário respondeu que não vê
necessidade: basta avisar que existe uma call, sem confirmar presença.

**Decisão.** O Tempus **não** tenta inferir se você está numa reunião. Nenhuma detecção de
microfone, câmera ou janela. Ele conhece o cronograma e avisa sobre ele; **você** comunica que
viu, clicando. O clique substitui integralmente a heurística de presença.

**Consequências.**

- Um subsistema Win32 inteiro sai do escopo (sessões de áudio, enumeração de dispositivos,
  polling de estado de captura) — provavelmente o mais frágil e menos testável do projeto.
  A Fase 5 encolhe e a Fase 3 fica mais simples.
- Some a categoria de falso positivo mais chata: outro app segurando o microfone e o Tempus
  concluindo que você está em reunião.
- **Custo aceito:** a barra não distingue "você está estourando a reunião" de "você saiu no
  horário". Mitigação embutida no modelo: `MeetingEnded` auto-limpa em 3 min, então reunião
  isolada não exige clique nenhum. Só o caso que realmente importa —
  `MeetingRanIntoNext`, a próxima já começou — persiste e exige o clique.
- Sinais renomeados para refletir que são baseados em cronograma:
  `MeetingStartedNotJoined` → `MeetingStarted`, `MeetingOverran*` → `MeetingEnded` /
  `MeetingRanIntoNext`.
- Revogável sem redesenho: se algum dia a detecção de mic for desejada, ela entra como um
  refinamento das condições dos mesmos sinais, sem mexer na arbitragem.

---

## D-007 — "Tarefas abertas" = todas as não concluídas

**Status:** Aceita · 2026-08-07 · com ressalva registrada

**Contexto.** O sinal `DayEnded` das 17:00 precisa contar "tarefas abertas do dia", e havia duas
leituras: apenas as que vencem hoje ou estão vencidas, ou todas as não concluídas. O usuário
escolheu **todas as abertas**.

**Decisão.** Todas as tarefas não concluídas, independente de data de vencimento.

**Ressalva registrada.** Se existir um backlog que nunca chega a zero, `DayEnded` fica **vermelho
todo dia**, e com a escalada de piscante do §1.1 isso significa piscar toda noite até o clique.
Pela regra do orçamento de vermelho, isso é o sinal clássico de vermelho virando papel de parede.

A leitura em que a decisão faz total sentido: `DayEnded` não é um alarme de exceção, é um
**ritual diário de encerramento** — "o dia acabou, este é seu backlog, reconheça". Um clique por
dia é um preço justo por isso. Aceito nesses termos.

**Consequências.** Implementar com a chave `DayEnded.CountMode` em `appsettings.json`, valores
`AllOpen` (padrão) e `DueTodayOrOverdue`. Assim, se depois de uma semana convivendo o vermelho
noturno incomodar, é uma linha de configuração e não uma mudança de código.

---

## D-008 — Feriados computados localmente: nacionais + PR + Pato Branco

**Status:** Aceita · 2026-08-07

**Contexto.** Os sinais de fronteira do dia (12:00 e 17:00) só devem disparar em dia útil. O
usuário definiu: seg–sex com feriados nacionais, e autorizou incluir estaduais e municipais de
Pato Branco/PR *se* houvesse fonte confiável — com a ressalva de que nesses dias ele não abre o
computador, então o custo de errar é zero.

**Decisão.** Tabela computada em código, sem rede e sem dependência externa. Datas fixas mais os
derivados da Páscoa, calculada pelo algoritmo de Meeus/Gauss (~30 linhas).

| Escopo | Datas |
|--------|-------|
| Nacionais fixos | 01/01, 21/04, 01/05, 07/09, 12/10, 02/11, 15/11, **20/11** (nacional desde 2024, Lei 14.759/2023), 25/12 |
| Nacionais móveis | Carnaval (Páscoa −48 e −47), Sexta-feira Santa (−2), Corpus Christi (+60) |
| Paraná | 19/12 — Emancipação Política do Paraná |
| Pato Branco | 29/06 — São Pedro Apóstolo, padroeiro (Lei Municipal 40/1970) · 14/12 — Emancipação Política do município |

Os municipais foram confirmados na lei municipal de 1970 e no calendário publicado pela própria
prefeitura, não inferidos.

**Consequências.** Funciona offline, para qualquer ano, sem manutenção anual. Carnaval e Corpus
Christi são ponto facultativo e não feriado legal, mas na prática se comportam como feriado —
entram. Emendas e pontos facultativos específicos da TOTVS ficam numa lista manual
`WorkDay.ExtraHolidays` em `appsettings.json`, porque não há fonte pública. Como o modo de falha
é "a barra fica âmbar às 12:00 num dia em que o usuário não está no computador", o custo de uma
data faltante é nulo — não justifica nem rede nem dependência.

---

## D-009 — Reuniões sobrepostas: o usuário declara o evento ativo

**Status:** Aceita · 2026-08-07

**Contexto.** Aceitar duas reuniões no mesmo horário é comum. Sem saber em qual você está, o fim
da primeira dispararia um `MeetingRanIntoNext` falso — o vermelho mais forte do produto, no caso
mais banal. Perguntado, o usuário sugeriu que o app pergunte em qual reunião está e use o horário
dela para determinar o fim.

**Decisão.** Um sinal `MeetingAmbiguous` de nível 1 pergunta qual é o **evento ativo**; o escolhido
passa a ser o único a alimentar os sinais de call. Mecânica completa em `SEVERITY.md` §8.

**Consequências.**

- Coerente com D-006: presença continua sendo **declarada** pelo usuário, nunca inferida pelo app.
  É a mesma filosofia do clique de reconhecimento, aplicada à desambiguação.
- A cláusula que mata o falso positivo é a definição de "próximo evento": o primeiro que começa
  após o fim do evento ativo **e não se sobrepõe a ele**. Reunião aceita em paralelo nunca conta
  como "próxima invadida".
- Exige um padrão determinístico antes da escolha (`accepted` > `tentative` > `needsAction`, então
  início mais cedo, então fim mais cedo), para a barra nunca ficar sem estado.
- Nível 1 e não 2: é uma pergunta, não um alarme. Não deve competir com sinais reais.

---

## D-010 — Identidade de ocorrência inclui o horário de fim

**Status:** Aceita · 2026-08-07

**Contexto.** Reconhecer um alerta suprime "aquela ocorrência", mas faltava definir o que
identifica uma ocorrência. Caso concreto levantado: reunião prorrogada no calendário depois do
reconhecimento — o sinal deve voltar? Resposta do usuário: sim.

**Decisão.** `occurrenceId = (eventId, início, fim)` para sinais de calendário, e
`(nomeDoSinal, dataLocal)` para os demais.

**Consequências.** Prorrogar uma reunião muda o `fim`, muda a identidade, e o sinal volta a
disparar — que é o correto, porque o reconhecimento anterior se referia a um compromisso que
terminava em outro horário. Mover ou reagendar tem o mesmo efeito, pela mesma razão.
Supressões de `DayEnded` e `MiddayCheckpoint` valem até a virada do dia e sobrevivem a reinício
do app; as de calendário não precisam sobreviver.

---

## D-004 — Keep e Chat fora de escopo

**Status:** Aceita · 2026-08-07

**Contexto.** O pedido inicial incluía Keep e Chat junto de Calendar, Tasks e Gmail.

**Google Keep.** A API existe e cobre edições Workspace, mas **exige admin com domain-wide
delegation via service account** — um usuário não consegue habilitar para si. Além disso foi
desenhada para casos de uso administrativos (DLP, varredura de conteúdo), não como API de
cliente pessoal. Custo de viabilizar é alto e o resultado é incerto.

**Google Chat.** Existe `spaces.getReadState` expondo `lastReadTime`, mas **não existe
endpoint de contagem de não-lidos**. Obter o número exigiria listar spaces, listar mensagens
de cada space e contar as posteriores ao `lastReadTime` — padrão N+1, custo de quota alto e
latência ruim para algo que atualiza a cada minuto.

**Decisão.** Ambos fora do escopo do v1. Keep provavelmente para sempre; Chat reconsiderável
se o Google publicar contagem de não-lidos.

**Consequências.** O v1 cobre Calendar + Tasks + contagem do Gmail. Registrado em
`SPEC.md` → Fora de escopo, para que nenhuma sessão futura tente "completar" a integração.

---

## D-005 — Fronteiras do dia: 12:00 é checkpoint, 17:00 é encerramento

**Status:** Aceita · 2026-08-07

**Contexto.** O pedido era "avisar que o dia terminou ao meio-dia" e "que terminou às 17:00" —
ambíguo entre dois encerramentos equivalentes e duas coisas semanticamente diferentes.

**Decisão.** Semânticas distintas: **12:00 = checkpoint de meio de jornada** (metade do dia
passou, o que estava planejado andou?) e **17:00 = fim de jornada**. Consequência direta nas
severidades: meio-dia chega no máximo a âmbar; 17:00 chega a vermelho, mas somente com tarefas
abertas. Detalhes em `SEVERITY.md` §2.2.

**Consequências.** Os horários são configuráveis (`appsettings.json` → `WorkDay`), com os
sinais desligados fora de dias úteis. A definição de "tarefas abertas do dia" permanece como
questão aberta em `SEVERITY.md` §5 e precisa ser fechada antes da Fase 3.

---

## D-011 — Slot esquerdo mostra o que vem a seguir, não a hora

**Status:** Aceita · 2026-08-10

**Contexto.** O layout inicial da barra tinha um relógio no slot esquerdo. Observação do usuário
ao ver a barra rodando: é redundante, porque o Windows já mostra a hora na própria taskbar, a
poucos centímetros. E aquele é o espaço mais visível da barra.

**Decisão.** O slot esquerdo passa a mostrar **o próximo compromisso**: hora absoluta em destaque
mais o título em texto secundário, truncado. Nunca colore.

Consequência de desenho que vale mais que a troca em si: os dois slots de texto ganham papéis
distintos e param de se repetir.

| Slot | Pergunta que responde | Presença | Cor |
|------|----------------------|----------|-----|
| Esquerdo (`NextUp`) | "o que vem depois?" | **sempre** | nunca |
| Meio (`Reason`) | "algo precisa de mim agora?" | **raro** | sim, é o único que colore |

Antes, o estado `Calm` usava a área de motivo para mostrar o próximo compromisso — o que fazia a
mesma informação aparecer em dois lugares e mantinha a área de alarme sempre ocupada. Agora
`Calm` tem motivo **vazio**, e uma barra calma tem o meio em branco. Isso é o objetivo, não uma
falta: o meio ocupado passa a significar, por si só, que algo precisa de atenção.

**Consequências.**

- `NextUpResolver.Resolve` é função pura de (agenda, agora) → próximo compromisso, testável sem
  UI nem rede (regra 8). Uma reunião **em curso** não conta como próxima: durante ela o que
  interessa é o que vem depois.
- Estados vazios são explícitos: `livre` quando não há mais nada hoje, `—` quando `Offline`.
  Nunca o último valor conhecido (`SEVERITY.md` §0).
- Clicar no slot abre a agenda e **nunca** reconhece alerta *(refinado em D-016: com link de Meet,
  o clique entra na call; sem link, segue abrindo a agenda)*. Reconhecer continua exclusivo da
  área de motivo, para que o gesto central do produto (D-006) tenha um lugar único e previsível.
- O timer de 1s perdeu sua razão original (atualizar o relógio), mas continua necessário para
  reafirmar a ordem Z — ver "Achado da Fase 2" em D-002.
- A barra ficou sem relógio próprio. Aceito: o Windows já tem um.

### Tarefas com hora exata: fora, por limitação do Google

O pedido original incluía "próxima ação minha, como uma tarefa com horário marcado exato" no mesmo
slot. **Não é possível pelo Google Tasks:** a API aceita um timestamp RFC 3339 mas grava apenas
ano/mês/dia — a hora é descartada, e não há como ler nem escrever hora de vencimento. Não é
limitação de biblioteca nem de cliente; é da API.

Alternativas apresentadas ao usuário e a escolha:

| Saída | Custo | Escolhida |
|-------|-------|-----------|
| Hora no início do título (`14:30 Ligar…`), com o Tempus lendo o `HH:mm` | Título carrega o horário | — |
| Hora só local no Tempus, ligada ao id da tarefa | Hora não existe fora da máquina; tarefa criada no celular nasce sem hora | — |
| Tarefa com hora vira evento no Calendar | Amplia escopo: a agenda passaria de somente-leitura para escrita | — |
| **Só reuniões alimentam o slot** | O pedido fica pela metade | ✅ |

**Decisão.** O slot é alimentado **somente pela agenda**. Tarefas continuam aparecendo apenas no
contador e no painel S2.

**Consequências.** `NextUp` não tem discriminador de origem — existe uma só, e um enum de um
membro seria código morto sugerindo uma decisão que foi recusada. Registrado na lista de
fora-de-escopo do `SPEC.md` para que nenhuma sessão futura tente "completar" isso.

---

## D-012 — Humor temporal: um segundo vocabulário de cor, ambiente

**Status:** Aceita · 2026-08-11

**Contexto.** Com dados reais na barra, o estado mais frequente virou "livre, sem reunião agora" —
e ele não dizia nada além de `livre`. Pedido do usuário: mostrar o próximo evento com contagem
regressiva em dias e horas, em cor calma, "porque vai ser um dos status que mais vai aparecer";
verde para bater o olho e saber que está livre; âmbar quando a reunião se aproxima; vermelho a 5
minutos. E, durante uma reunião, o que importa é quanto falta para ela acabar e o que vem depois —
com o título, não só "há uma próxima".

**Decisão.** O slot esquerdo ganha **vocabulário de cor próprio**, independente da escala de
severidade: `TimeMood` com Free / InMeeting / Approaching / EndingSoon / Imminent. Tabela completa
em `SEVERITY.md` §1.5.

**Duas tensões com o modelo existente, e como foram resolvidas:**

**1. Vermelho a 5 minutos estoura o orçamento de vermelho.** A §1 dizia 0–2× por dia, com ~3 como
sinal de que o modelo está errado. Um dia com 6 reuniões produz 6 vermelhos de contagem regressiva.

Resolvido **por forma, não por cor**: os dois vermelhos passam a ser distinguíveis pelo
comportamento. O de contagem regressiva dura no máximo 5 minutos, **se resolve sozinho** quando a
reunião começa e nunca pisca. O de alarme (`MeetingRanIntoNext`, `DayEnded`) **escala para
piscante em 5 min** e só sai com clique. Mesma cor, urgências distinguíveis sem esforço.

**2. A invariante I1 dizia "uma cor por vez".** Agora há duas áreas coloridas simultâneas.

I1 foi revista, não abandonada: no máximo **duas** áreas, com vocabulários distintos — ambiente e
alarme. O que continua proibido é **dois alarmes** competindo. A separação de forma sustenta isso:
texto tingido é ambiente, preenchimento é alarme, e o slot de tempo só preenche nos dois humores
que pedem antecipação.

**Consequências.**

- `NextUp` e `NextUpResolver` foram substituídos por `TimeStatus` e `TimeStatusResolver`, que
  respondem uma pergunta mais ampla — não só "o que vem depois" mas "como está meu tempo". Função
  pura de (agenda, agora, limiares), testável sem UI nem rede (regra 8).
- Verde entra na paleta **dessaturado**. É o estado mais frequente do dia; saturado, a barra
  gritaria o tempo todo.
- O layout inverteu as prioridades de largura: o slot de tempo passou a flexível e o chip a
  `Auto`. Assim o alarme sempre cabe inteiro e o ambiente é que trunca — a ordem certa.
- O redesenho de estado passou de 20s para 5s. Não custa rede, só recontagem de tempo, mas a 20s
  uma contagem regressiva ficaria visivelmente atrasada perto da virada de minuto.
- Limiares em `appsettings.json` → `Time`, para ajustar sem recompilar.

---

## D-013 — Estourar reunião é dano a terceiros, não atraso próprio

**Status:** Aceita · 2026-08-11 · **corrige D-012**

**Contexto.** D-012 fez `EndingSoon` colorir apenas quando havia outra reunião em seguida. O
raciocínio era: "se tem a tarde livre, acabar tarde não custa nada". O usuário corrigiu com o
incidente que originou o projeto — segurou a equipe inteira numa week review até **12:22**, quando
o horário de todos terminava 12:00.

O erro do meu raciocínio foi o referencial. Eu media o custo pela **minha** agenda; o custo real é
o **tempo das outras pessoas**, que foi comprometido pela duração marcada. Uma reunião de uma hora
tem uma hora do dia de cada participante reservada, e estourar consome tempo alheio
independentemente do que exista depois na minha agenda. Ter a tarde livre não devolve os 22
minutos a quem estava na sala.

**Decisão.**

1. `EndingSoon` dispara **sempre** que a reunião atual está a ≤5 min do fim marcado, com ou sem
   evento seguinte.
2. Novo humor `Overrun`: vermelho preenchido quando o horário marcado já passou, com prioridade
   sobre "próxima reunião". Acusa por 10 min (`Time.OverrunMinutes`) e some.
3. Novo indicador de **fronteira do dia** (`SEVERITY.md` §1.6): contagem regressiva para 12:00 e
   17:00, em gradiente verde→âmbar→vermelho, no canto esquerdo da barra.

**Por que a fronteira do dia é um terceiro elemento, e não mais um estado:**

O humor temporal cobre o caso "estou numa reunião que vai acabar". A fronteira cobre o outro, que
o incidente também expõe: estar trabalhando **sem agenda nenhuma** e perder a hora. São perguntas
diferentes, e a fronteira é um gradiente contínuo em vez de uma escala de estados — não cabe na
gramática do §0.5. Por isso ocupa espaço próprio, pequeno, e desaparece por completo fora da
janela de aviso.

**Consequências.**

- `Overrun` some sozinho depois de 10 min. Sem detecção de presença (D-006), o app não sabe se
  você encerrou; insistir para sempre viraria ruído e o vermelho perderia valor. O aviso vale no
  intervalo em que o dano acontece.
- Falso positivo aceito: encerrar 2 min antes do horário marcado ainda produz `Overrun` na virada.
  A assimetria justifica — o custo de um alerta desnecessário é uma olhada; o de um alerta que não
  veio foi a week review das 12:22.
- Feriados de D-008 saíram do papel: nacionais, do Paraná e de Pato Branco, com Páscoa por
  Meeus/Jones/Butcher. Sem eles, a barra faria contagem regressiva de expediente em feriado.
- `WorkDay` agora é lido do `appsettings.json`; os horários deixaram de ser suposição no código.

---

## D-014 — Defesa da ordem Z é orientada a evento, não a polling

**Status:** Aceita · 2026-08-12 · **refina o "Achado da Fase 2" do D-002**

**Contexto.** Relato do usuário: a barra "some e volta" conforme ele clica em aplicativos e navega
pelo Windows — comportamento que antes não aparecia. Medido com sonda externa (`EnumWindows` em
ordem Z contra `Shell_TrayWnd`, mais `SHQueryUserNotificationState`), amostragem de 120ms, 60s de
navegação real:

| Sinal | Resultado | O que descarta |
|-------|-----------|----------------|
| `IsWindowVisible` | `True` em 100% das amostras | `HideBar()` nunca roda |
| `WS_EX_TOPMOST` | `True` sempre | o estilo nunca se perde |
| `SHQueryUserNotificationState` | `5` (AcceptsNotifications) sempre | `ShouldYieldScreen()` nunca disparou |
| Retângulo | imutável durante todo o período | `BarPlacement.Compute` nunca devolveu `null` |
| Ordem Z | **oscila: taskbar salta para a frente da barra** | ⬅ a causa |

**A barra nunca desaparece; o `Shell_TrayWnd` passa na frente dela.** 27 episódios em 60s, todos
correlacionados com troca de janela ativa. Tempo até voltar: 125ms a 1s, mediana ~500ms — a
assinatura do heartbeat de 1s pegando o problema numa fase aleatória.

Ou seja: o diagnóstico do D-002 estava certo e a mitigação funciona. O defeito é a **latência**
dela. E o intervalo não é cosmético — enquanto dura, os pixels pertencem ao explorer e a barra
fica **surda a cliques**, que é o gesto central do produto (D-006).

O código de posicionamento não mudava desde a Fase 2, e o heartbeat de 1s já estava lá. O que
mudou foi a frequência com que o explorer se reergue (máquina em 25H2, build 26200.8973).
Observação, não causa provada — e irrelevante para a correção, porque a fragilidade é estrutural.

**Decisão.** `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, …, WINEVENT_OUTOFCONTEXT)` de escopo global.
Trocar janela ativa é justamente o instante em que o explorer ergue a taskbar, então o evento
existe — o D-002 dizia "não há evento para isso" e isso estava errado.

Três caminhos, do mais rápido ao mais lento, porque nenhum sozinho basta:

1. **Evento** — reafirma `HWND_TOPMOST` no instante da troca de foco.
2. **Rajada de acomodação** — 24 reafirmações a 16ms (~380ms). O explorer ergue a taskbar *depois*
   que a ativação se completa, então a reafirmação imediata chega cedo demais e perde a corrida.
   Medido: às `21:59:34.801` a barra caiu para trás com o foco já estabilizado em outra janela.
3. **Heartbeat de 1s** — rede de segurança para erguidas sem troca de foco, e para o caso de o
   sistema recusar o hook.

**O intervalo da rajada é o teto da latência**, e isso só ficou visível ao medir a 5ms — a sonda
original, a 120ms, fazia aliasing e mostrava 9 episódios onde havia 64. Com rajada de 50ms as
durações saíam em múltiplos exatos de 15,6ms (15/31/47/63/79), que é a granularidade padrão do
timer do Windows: uma erguida logo após um tick espera o próximo. Baixar o intervalo para 16ms faz
o piso do timer virar o teto da falha.

| Reafirmação | Episódios em 60s | Pior caso | Média |
|-------------|------------------|-----------|-------|
| Só heartbeat de 1s (antes) | — | ~1000ms | ~500ms |
| Evento + rajada de 50ms | 64 | 79ms | 43ms |
| **Evento + rajada de 16ms** | 25 | **33ms** | **22ms** |

A contagem de episódios não é comparável entre execuções — depende de quanto se navegou. As
durações são.

O timer principal subiu de `DispatcherPriority.Background` para `Normal`: em Background o tick é
adiável indefinidamente quando a thread de UI está ocupada, e é exatamente aí que a ordem Z
precisa dele.

**Alternativas descartadas:**

| Opção | Motivo da recusa |
|-------|------------------|
| Encurtar o heartbeat para ~100ms | 10 `SetWindowPos`/s para sempre. Viola a regra 9 e o critério de aceite 10, e ainda deixaria 100ms de buraco. Trata o sintoma. |
| `Shell_TrayWnd` como **owner** da barra (`GWLP_HWNDPARENT`) | Resolveria de vez: janela owned fica acima do owner por garantia do gerenciador de janelas, sem polling nenhum. Mas **janelas owned são destruídas junto com o owner** — restart do explorer mataria a barra. Não é `SetParent`, e ainda assim compartilha o defeito que o D-002 recusou. |
| `SetWindowBand` (ordinal não documentado do user32) | Exige `uiAccess=true`, que exige binário assinado instalado em `Program Files`. Desproporcional para ferramenta pessoal, e sobre API não documentada. |

**Consequências.**

- O `ForegroundWatcher` guarda o delegate em **campo**, não em local nem lambda inline. É a ponte
  que o sistema chama de fora do runtime; sem referência forte viva o GC o coleta e o callback
  aterrissa em memória liberada. Falha rara, distante da causa e ilegível no dump.
- O hook é instalado na thread de UI de propósito: `WINEVENT_OUTOFCONTEXT` entrega o callback pela
  fila de mensagens da thread que instalou, então o evento já chega na thread certa — sem
  marshaling e sem lock no caminho quente.
- A rajada se autodesarma. Em repouso nenhum timer de 16ms sobrevive, para não gastar o critério
  de aceite 10 com um problema que só existe enquanto se navega. **Verificado:** medindo em fatias
  de 2s e contando só as que não tiveram troca de foco — o repouso verdadeiro, sem exigir máquina
  parada — deu **0,053% de um núcleo em 116,9s**, abaixo dos 0,13% medidos na Fase 2. A medição
  ingênua, feita logo após navegar, tinha dado 1,094% e era artefato: a janela pegava a rajada
  ainda armada.
- **O que isto não resolve:** a barra continua disputando a mesma banda topmost que a taskbar, e
  toda vitória é por reação. A janela de falha caiu de ~500ms para 22ms na média e 33ms no pior
  caso — um a dois frames —, mas **não é zero**. Descer abaixo disso exigiria `timeBeginPeriod`,
  que muda a resolução de timer da máquina inteira e é desproporcional para uma ferramenta
  pessoal. Se algum dia um ou dois frames deixarem de bastar, a resposta é o `TraySurface` do
  D-002, não uma quarta camada de reafirmação.
- A sonda de ordem Z vale mais que a comparação de retângulos, como o D-002 já suspeitava. Ela é
  melhor que o `WindowFromPoint` previsto lá: compara a ordem inteira em vez de perguntar por um
  pixel só, então não tem falso negativo quando a sobreposição é parcial.

---

## D-015 — O slot de tempo lidera com rótulo de estado em todos os humores

**Status:** Aceita · 2026-08-14 · **refina D-011 e D-012**

**Contexto.** Relato do usuário durante uma reunião: "sumiu o livre/ocupado do lado esquerdo".
Nada tinha quebrado — ele estava em reunião, o humor era `InMeeting`, e o texto era
`Fórum de Lideranças · faltam 37 min`. Correto conforme o D-012. E "Ocupado" nunca existiu como
rótulo em lugar nenhum do código.

O que o relato expôs foi uma **assimetria de vocabulário** que o D-012 tinha introduzido sem
perceber: o slot liderava com palavra de estado em um humor e com título de reunião nos outros.

| Humor | Antes |
|-------|-------|
| `Free` | **Livre** · Daily em 2h15 |
| `InMeeting` | Refino · faltam 25 min |
| `Overrun` | Weekly · passou 22 min |

A nota de design do §1.5 até justificava: "perto da hora o título passa à frente, porque aí o que
importa é *o que* vai começar". O raciocínio media a informação isolada, e o erro foi esse. A
pergunta que o slot responde (D-011) é "como está meu tempo agora", e ela se lê **de relance** —
o olho bate num ponto fixo. Com a posição do estado variando conforme o humor, é preciso *ler a
frase inteira e interpretá-la* antes de saber se está livre ou ocupado. Um slot que exige leitura
não é um sinal ambiente; é texto.

**Decisão.** Todo humor lidera com rótulo de estado, e o título da reunião vem depois como
detalhe. Tabela completa em `SEVERITY.md` §1.5.

| Humor | Rótulo | Exemplo |
|-------|--------|---------|
| `Free` | `Livre` | `Livre · Daily em 2h15` |
| `Approaching` | `Em breve` | `Em breve · Daily em 12 min` |
| `Imminent` | `Começando` | `Começando · Daily em 4 min` |
| `InMeeting` | `Ocupado` | `Ocupado · Refino, faltam 25 min → Review` |
| `EndingSoon` | `Encerrando` | `Encerrando · Refino, faltam 4 min` |
| `Overrun` | `Estourou` | `Estourou · Weekly, passou 22 min` |
| `OffHours` | `Dia Encerrado` · `Almoço` · `Folga` | já era só rótulo, inalterado |

**Consequências.**

- O rótulo **duplica a informação da cor**, de propósito. Cor sozinha exige memorizar o mapa
  (azul = em reunião?); com a palavra, o mapa é dispensável e a barra segue legível para quem
  não distingue as cores bem.
- O texto ficou mais longo, e isso reabriu um defeito de layout que tinha passado batido: o
  `StackPanel` horizontal introduzido junto com o `Lookahead` mede os filhos com largura
  **infinita**, o que desliga o `TextTrimming` e troca as reticências por um corte a seco na
  borda da coluna. Trocado por um `Grid` de duas colunas, onde a situação temporal trunca com
  reticências no espaço que sobra.
- `Approaching` e `Imminent` ganharam rótulo próprio (`Em breve`, `Começando`) em vez de herdar
  `Livre`. Tecnicamente você *está* livre nos dois, mas dizer "Livre" a 4 minutos de uma reunião
  seria a mesma mentira confortável que o D-012 corrigiu no verde das 21h.
- Nada muda na cor, na forma nem nos limiares. É mudança de texto e de layout apenas — as
  invariantes I1–I8 seguem valendo sem revisão.

---

## D-016 — Clicar no slot de tempo entra na call

**Status:** Aceita · 2026-08-14 · **refina D-011**

**Contexto.** Pedido do usuário: "no aviso de call, gostaria de clicar em cima e já direcionar pro
meet". O D-011 tinha definido que clicar no slot abre a agenda (S3), e o painel de agenda já
sabia abrir o Meet de um item — mas isso custava dois cliques e um painel intermediário para a
ação mais óbvia que a barra pode oferecer enquanto uma reunião acontece.

Toda a infraestrutura já existia e estava ociosa: `AgendaItem.MeetUrl`, a extração do
`HangoutLink` com fallback para `ConferenceData.EntryPoints` em `GoogleSync.ExtractMeetUrl`, e o
evento `MeetingActivated` ligado ao `Open(url)` no `App`. O que faltava era o slot de tempo
carregar a URL da reunião que ele já nomeia.

**Decisão.** `TimeStatus` ganha `MeetUrl`, preenchido com o link da reunião a que o estado se
refere. Clicar no slot **entra na call**; sem link — reunião presencial, por telefone, ou nenhuma
reunião em vista — segue abrindo a agenda, como antes.

Qual reunião, por humor:

| Humor | Link de qual reunião |
|-------|---------------------|
| `InMeeting`, `EndingSoon` | a que está em curso |
| `Approaching`, `Imminent`, `Free` | a próxima |
| `Overrun` | a que invadiu, se houver; senão a que estourou |

O caso `Overrun` é o único que exigiu escolha. Às 12:22 da week review que originou o projeto
(D-013), o que interessa é entrar na que **já começou**, não voltar para a que devia ter acabado.

**Consequências.**

- **O gesto de reconhecer não muda.** O clique no chip de motivo continua sendo exclusivamente
  "eu vi" (D-006, invariante I3). Só o slot de tempo mudou de destino, e ele nunca reconheceu
  alerta nenhum — a separação que o D-011 estabeleceu segue intacta.
- A agenda perderia seu único atalho de clique esquerdo durante uma reunião, então o menu de
  contexto ganhou **"Entrar na call"** em destaque no topo, e "Abrir agenda" continua lá. Nenhuma
  superfície fica inalcançável.
- O tooltip passa a anunciar o destino do clique — "Clique para entrar na call" ou "Clique para
  abrir a agenda" —, porque a mesma área agora faz duas coisas conforme o dado, e adivinhar qual
  seria pior que um clique a mais.
- `Offline` continua tendo prioridade sobre tudo: sem sincronização o clique pede re-consent
  (D-003), porque um link guardado de antes não é dado atual (regra 10).

---

## D-017 — Call é um conceito com provedor, achada em cascata

**Status:** Aceita · 2026-08-14 · **refina D-016**

**Contexto.** Reunião de Zoom com o link **dentro do convite** não ficava clicável. A causa estava
no nome do campo: `AgendaItem.MeetUrl` carregava a suposição de que toda call é Meet, e
`ExtractMeetUrl` lia só `hangoutLink` e `conferenceData.entryPoints`.

Isso cobre o Meet e cobre o Zoom criado pelo **add-on** do Zoom, que preenche `conferenceData`.
Não cobre o caso comum de verdade: convite que chegou por e-mail ou veio do Outlook, com o link
colado no corpo. Para o app, esse evento simplesmente não tinha call.

**Decisão.** `Conference { Url, Provider }` substitui a string. Detecção em cascata:

| Ordem | Fonte | Aceita |
|-------|-------|--------|
| 1 | `conferenceData.entryPoints[video]` | qualquer URL |
| 2 | `hangoutLink` | qualquer URL |
| 3 | `location` | **só provedor conhecido** |
| 4 | `description` | **só provedor conhecido** |

**Por que texto livre é mais restrito que campo estruturado.** As duas primeiras fontes são
preenchidas pelo Google: o que está ali *é* a call, então qualquer URL serve. As duas últimas são
texto digitado por gente, e um convite carrega link de rastreador de e-mail, de documento anexo e
de descadastro. Abrir o primeiro que aparecer seria um clique no escuro — e o clique é o gesto
central do produto (D-006). De texto livre só sai o que dá para reconhecer pelo **host**.

Classificação por host, nunca por substring: `marketing.exemplo.com/zoom/promo` não é uma call.

**Consequências.**

- O `?pwd=` do Zoom **não pode ser cortado** — sem ele a call pede senha na entrada. A regex é
  permissiva à direita e para só no que não pode fazer parte de uma URL. Verificado com 11 casos,
  incluindo `&amp;` em HTML, subdomínio corporativo, e ponto final de frase grudado no link.
- Descrição do Google Calendar vem em HTML, então entidades são decodificadas antes de
  classificar. Sem isso o `&amp;` quebraria o query string.
- **Teams entra na detecção sem entrar no vocabulário de cor.** Foram pedidos Meet e Zoom; deixar
  Teams fora da detecção reproduziria a queixa original, e dar cor a ele gastaria legibilidade
  numa distinção que ninguém pediu.
- `TimeStatus.MeetUrl` virou `CallUrl` e carrega **só a URL**: a barra não sinaliza serviço.

### Cor de provedor: no painel S3, e só lá

O usuário pediu ícone dos aplicativos na barra, e depois escolheu confinar a distinção ao painel:
azul Zoom, verde Meet, no S3.

É a escolha certa por dois motivos que valem registro. Primeiro, a invariante I1 governa **a
barra** ("no máximo duas áreas coloridas"), e os painéis estão fora dela — no S3 a cor de marca
não disputa com a escala de severidade nem com o humor temporal. Segundo, na largura da barra o
ícone caberia com ~13px, e nesse tamanho um logo é um borrão colorido: o que distinguiria seria a
cor, exatamente o recurso que a regra 1 manda economizar.

**Onde a cor entra, depois de uma revisão.** A primeira versão usou um ponto colorido de 8px. O
usuário pediu para trocar por **fundo do título** — e liberou o ponto para outra coisa, que virou
o D-018. Fundo é a escolha melhor: um ponto de 8px força o olho a procurar um alvo pequeno, e o
título é o que ele já está lendo.

O fundo é **tingido, não chapado** (alpha `0x4D`, metade disso em evento passado). Quase todo
evento tem call; com a cor saturada o painel viraria uma parede de azul e verde onde nada se
destaca, e a cor deixaria de informar.

A faixa vertical da coluna 0 **continua sendo estado** — ela já usa azul (`InfoBackground`) para
"próxima", e pintá-la de azul-Zoom seria dois significados no mesmo elemento.

As cores de marca ficam **fora** do record de tema: identidade não acompanha claro/escuro.

---

## D-018 — A bolinha do S3 marca o convite pendente, não o serviço

**Status:** Aceita · 2026-08-14 · **usa o espaço liberado pelo D-017**

**Contexto.** Ao mover a cor do serviço para o fundo do título, o ponto da coluna 2 ficou livre. O
usuário destinou-o ao que considera mais útil: *"marcar os eventos que aceitei participar ou não
ainda… acho mais importante saber qual eu aceitei do que para qual app vai abrir."*

Faz sentido, e o motivo é que as duas informações têm naturezas diferentes. O serviço é
**contexto** — você descobre ao clicar, e errar custa um clique. A resposta pendente é uma
**tarefa** — ninguém a descobre por acaso, e ela some da sua cabeça até o convite virar reunião
começando sem você.

**Decisão.** `Rsvp { None, Accepted, Tentative, NeedsAction }`, lido de
`attendees[self].responseStatus`. `Declined` não existe no enum: evento recusado é descartado
antes de virar `AgendaItem` (§2.1). Evento sem convidados é `None` e não ganha marca — é
compromisso próprio, não há o que responder.

| Estado | Marca | Cor |
|--------|-------|-----|
| Aceitei | ✓ | cinza |
| Talvez | ? | cinza |
| Não respondi | ● cheio | **âmbar** |
| Sem convidados | — | — |

### A primeira versão falhou, e o erro vale mais que a correção

A tentativa inicial distinguia por **preenchimento**: círculo cheio = aceitei, anel vazado = não
respondi. Monocromático, para economizar cor conforme a regra 1.

Não funcionou. Palavras do usuário: *"tenho que me aproximar muito da tela pra saber se tão
preenchidos ou não."*

O erro não foi a escolha da cor — foi otimizar para a regra em vez de para o olho. A regra 1 diz
que cor é escassa, e daí eu concluí "então não use cor", ignorando que a barra fica na taskbar e é
lida de relance, a meio metro. **Cheio e vazado no mesmo diâmetro são o par de formas menos
distinguível que existe**, porque o contorno é idêntico e só o miolo muda.

Duas correções, e as duas importam:

1. **Formas genuinamente diferentes** — ✓, ?, ● — em vez de variações de um círculo. A leitura
   deixa de depender de acuidade.
2. **Só o pendente colore.** As três marcas não têm o mesmo peso: aceito é o estado normal e não
   precisa competir com nada; "não respondi" é o único que exige ação. Ausência de destaque é
   informação — se nada está âmbar, não há convite parado.

O âmbar não é arbitrário: é a cor de *atenção* do próprio modelo de severidade. Usá-la para "você
me deve uma resposta" faz o painel falar o mesmo idioma da barra, em vez de inventar um terceiro.

**Consequências.**

- O ✓ reusa `E73E`, o mesmo glifo do contador de tarefas. Check já significa "resolvido" no resto
  da barra; repetir o símbolo é coerência.
- Convém observar se o âmbar do ponto briga com o âmbar do selo "agora" na mesma linha. São
  elementos de forma e posição distintas — ponto à esquerda, pílula de texto à direita — mas é o
  tipo de coisa que só o uso decide.
- **Regra aprendida, que vale além deste caso:** distinção por preenchimento não sobrevive à
  distância de leitura da barra. Quando forma for o único recurso, use formas de silhueta
  diferente — nunca a mesma silhueta cheia e vazada.

---

## D-019 — Pausas de descanso: locais, móveis e opt-in

**Status:** Aceita · 2026-08-17 · pedida em uso, fora do plano das fases

**Contexto.** Pedido do usuário: 15 minutos de descanso na manhã e na tarde, encaixados no meio de
cada período, numa janela sem call marcada. A justificativa é levantar da cadeira.

**Decisão.** `BreakPlanner.Plan(agenda, agora, expediente, opções)` — função pura (regra 8) que
devolve até duas janelas. Manhã é `StartHour`→`MiddayHour`, tarde é `LunchEndHour`→`EndHour`, então
a feature herda o expediente configurável do D-007 sem inventar horário próprio.

A busca varre a grade de 5 em 5 minutos e escolhe a janela livre **mais próxima do meio** do
período — não a primeira que couber. Cinco minutos é fino o bastante para achar folga em agenda
cheia e grosso o bastante para a pausa cair em horário redondo, que é como as pessoas leem hora.

### Vive só no Tempus

Não vira evento no Google Calendar. Dois motivos, e o segundo é o que decide:

1. Escrever no Calendar exigiria subir de `CalendarReadonly` para escopo de escrita, com novo
   consent numa conta corporativa — custo real por um benefício hipotético.
2. **Um evento real ficaria parado.** A pausa é recalculada a cada rodada de sync, então uma
   reunião marcada em cima empurra o descanso para a próxima janela livre. Evento no Calendar
   perderia exatamente a propriedade que torna a feature útil em agenda que muda.

Promover a evento real fica reservado para *se* o problema virar "colegas marcam por cima". O
cálculo da janela é o mesmo nos dois casos, então nada se joga fora.

### Três níveis de controle, e por que são três

| Nível | Onde | Padrão |
|-------|------|--------|
| Existe a funcionalidade? | `appsettings` → `Breaks.Enabled` | **false** |
| Tem folga hoje? | automático, todo dia | sim |
| Quero a de hoje? | menu da barra | sim, até dispensar |

O liga/desliga **não** está no menu de propósito. Se estivesse, o dia ruim — o dia em que a agenda
está cheia e a pausa importa mais — seria exatamente o dia em que a mão desligaria a feature de
vez. Separar "não quero hoje" de "não quero nunca" preserva a segunda decisão de ser tomada a
frio.

Nascer desabilitada é pedido explícito do usuário: quem nunca ouviu falar da folga não deve ser
surpreendido por um bloco novo no painel.

### Detalhes que custaram pensamento

**A dispensa guarda a data, não um booleano.** Um booleano exigiria limpeza na virada do dia, e um
dia em que a limpeza não roda é um dia sem pausa. Comparar datas não tem esse modo de falha:
qualquer dia diferente do gravado tem folga. Fica em `%APPDATA%\Tempus\break-dismissed` porque a
barra reinicia — dispensar e ver a pausa voltar dez minutos depois ensinaria a não confiar no
gesto.

**O filtro é `fim > agora`, não `início > agora`.** Uma pausa em curso continua candidata e vence
por estar no ideal. Com o filtro ingênuo o relógio empurraria a pausa para a frente a cada rodada
e ela nunca terminaria de acontecer.

**Eventos de dia inteiro não bloqueiam.** Ocupam as 8 horas e não impedem ninguém de levantar da
cadeira. Tratá-los como ocupado apagaria as duas pausas de qualquer dia com férias ou aniversário
no calendário.

**Nenhuma cor nova** (regra 1). No painel a pausa é itálico no verde de "Livre", sem fundo de
serviço e sem marca de convite — distinguida por forma, não por tom. Na barra ela toma o slot do
lookahead durante os 15 minutos, em vez de ganhar espaço próprio: "levante da cadeira" vale mais
que "próximo compromisso" naquele momento, e a barra não cresce por um estado que dura 15 min.

**Consequências.** A contagem do cabeçalho do S3 ignora pausas — ela responde "quantos
compromissos tenho", e descanso não é compromisso. O vão entre linhas passa a considerar a pausa,
senão o painel diria "45 min livre" num intervalo que já tem descanso dentro.

Verificado em 2026-08-17 contra a agenda real: pausa da tarde em 14:50–15:05 (meio da tarde é
14:52:30), ausência correta da pausa da manhã ao meio-dia, e o ciclo dispensar→restaurar gravando
e limpando o arquivo. **Não verificado visualmente:** o texto ambiente na barra, que só aparece
durante os 15 minutos e com o humor em `Free`.

---

## D-020 — Configuração em duas camadas, com tela própria

**Status:** Aceita · 2026-08-17

**Contexto.** O e-mail do usuário estava **commitado** no `appsettings.json`, e qualquer ajuste
exigia editar JSON na pasta de instalação. Pedido: tirar o dado pessoal do código e dar uma tela,
principalmente para a primeira execução.

Uma suspeita foi verificada e descartada antes de virar decisão: achei que atualizar o app
sobrescreveria a configuração, mas o `install.ps1` **já preserva** o `appsettings.json` existente
no destino. Perda em atualização não era o problema.

**Decisão.** Duas camadas:

| Camada | Onde | Papel |
|--------|------|-------|
| Padrão de fábrica | `appsettings.json`, no repo | valores iniciais, **sem dado pessoal** |
| Escolhas do usuário | `%APPDATA%\Tempus\settings.json` | sobrepõe o padrão |

`%APPDATA%` e não a pasta de instalação por três motivos: fica ao lado do `client_secret.json` e
dos tokens, num lugar só para "seus dados"; não exige permissão de escrita em Program Files; e
está **fora do repositório por construção**, então não há como commitar por acidente — que é
exatamente o acidente que originou esta decisão (regra 5).

Cada campo do arquivo do usuário é **anulável**, e `null` significa "não escolhi, use o padrão".
Sem isso um campo ausente viraria o default do tipo — `StartHour = 0` em vez de 8 — e o arquivo do
usuário sobrescreveria em silêncio configuração que ele nunca tocou.

**Escopo da tela:** e-mail, expediente e pausas. O ajuste fino — limiares de tempo, query do Gmail,
âncora e largura da barra — fica no JSON. São coisas que se mexe uma vez e nunca mais; promovê-las
a UI custaria manutenção a cada opção nova sem ganhar uso.

### Primeira execução exige o e-mail

Escolha do usuário, tomada com a ressalva à vista: o app **funciona** sem `login_hint` — ele só
deixa de pré-selecionar a conta no consent semanal (D-003). Exigir o campo bloqueia algo que
tecnicamente rodaria.

Mitigação: na primeira execução o botão vira **"Sair"** em vez de "Cancelar". Se fechar encerra o
app, o rótulo tem que dizer isso — um "Cancelar" que mata o processo seria uma armadilha.

O modo `--demo` escapa da exigência: ele existe para exercitar a UI sem conta, e pedir conta o
inutilizaria.

### Três achados da verificação

**Diálogo herda o não-foco da barra.** A barra é `WS_EX_NOACTIVATE` (D-002), e a janela aberta a
partir do menu dela nascia **atrás de tudo** — o usuário clicaria em "Configurações…" e concluiria
que nada aconteceu. Corrigido com `SetForegroundWindow` explícito. Vale para qualquer janela futura
aberta a partir da barra.

**Controles do WPF não herdam tema.** A primeira versão ficou com texto escuro sobre painel escuro
e caixas brancas no meio do dark. `Window.Foreground` não alcança `TextBox`, `CheckBox` nem
`Button`. Resolvido com estilos usando `DynamicResource`, alimentados pela `Palette` em código. A
barra de título é do Windows e precisou de `DWMWA_USE_IMMERSIVE_DARK_MODE` à parte.

**Propriedade derivada vazava para o arquivo.** `HasLoginHint` é calculada, e o serializador a
gravava no JSON. Inofensiva na leitura, mas passa a mentir assim que o e-mail muda por fora.
`[JsonIgnore]`.

**Consequências.** `appsettings.json` passa a ser documentação de padrões, não configuração viva —
o comentário do `LoginHint` agora diz explicitamente para não preencher ali. Quem já tinha o e-mail
no arquivo antigo não perde nada: a tela abre na primeira execução e grava no lugar novo.

---

## D-021 — Reafirmar a ordem Z só quando ela estiver errada

**Status:** Aceita · 2026-08-18 · **corrige o D-014**

**Contexto.** O menu de contexto e as dicas da barra afundavam atrás dela enquanto o usuário os
lia. Não era o explorer: era o próprio Tempus.

`AssertTopMost()` chamava `SetWindowPos(HWND_TOPMOST)` **incondicionalmente**, a cada troca de
janela ativa e a cada segundo do heartbeat. `HWND_TOPMOST` não significa "continue onde está":
coloca a janela no **topo da faixa topmost** — acima dos popups da própria barra, que são janelas
separadas e estavam legitimamente à frente dela.

Ou seja, o remédio do D-014 tinha um efeito colateral que só aparecia com um popup aberto, que é
exatamente o momento em que ninguém está olhando para bugs de ordem Z.

**Decisão.** Perguntar antes de agir: `NativeMethods.IsInFrontOf(barra, Shell_TrayWnd)` percorre a
ordem Z de cima para baixo com `EnumWindows` e para assim que encontra as duas janelas. Só quando a
barra estiver **atrás** da taskbar é que `SetWindowPos` é chamado.

No caso comum — barra já no lugar certo — nenhuma chamada acontece, e os popups ficam onde estão.

**Consequências.**

- O D-014 previa uma "sonda de ordem Z" que **nunca foi escrita**: o código reafirmava sem medir, e
  o roadmap registrava a sonda como pronta. Agora ela existe de fato.
- Menos trabalho em repouso, não mais: troca uma chamada de `SetWindowPos` por segundo por uma
  enumeração que aborta nas duas primeiras janelas de interesse.
- Se a barra cair atrás **enquanto** um menu está aberto, a reafirmação volta a cobri-lo. É raro e
  se resolve reabrindo o menu; corrigir esse caso exigiria suspender a proteção justamente quando
  ela é necessária, o que troca um incômodo por uma falha grave.

Verificado com o menu aberto por 6 segundos — seis rodadas do heartbeat — conferindo a ordem Z a
cada 1,2s: o popup permaneceu à frente da barra em todas as amostras.

---

## D-022 — Um compromisso por vez, e o clique só entra na call quando for hora

**Status:** Aceita · 2026-08-18

**Contexto.** Dois defeitos de leitura relatados em uso, com a mesma origem em
`TimeStatusResolver.Between`.

A barra mostrava **dois compromissos lado a lado**: o slot de estado dizia "Livre · Treinamento do
GWS em 3h30" e o slot de lookahead dizia o primeiro compromisso de *amanhã*. São fatos diferentes,
mas lidos de relance viram "tenho duas reuniões" — a pergunta errada, respondida errado. Pior: os
dois disputavam largura, e o título do compromisso de hoje saía truncado.

E o clique no slot **entrava numa call que só começaria horas depois**, porque `CallUrl` recebia
`next.Conference?.Url` em qualquer humor, inclusive `Free`.

**Decisão.**

`TimeStatus.NamesAnEvent` marca que o texto de estado já nomeia um compromisso. O lookahead cede
quando ela é verdadeira — um compromisso por vez, e o mais próximo ganha. Como efeito colateral
bem-vindo, o título deixou de truncar: sem o vizinho disputando largura, cabe inteiro.

`CallUrl` só é preenchido a partir de `Imminent` (5 min). Antes disso o clique abre a agenda.
Entrar cedo numa reunião é legítimo aos 5 minutos e é um estrago silencioso às 4 horas: você cai
numa sala vazia sem perceber que entrou, e quem chegar depois vê que você estava lá.

**Consequências.** `InMeeting` e `Overrun` também marcam `NamesAnEvent`, por consistência — hoje o
lookahead nem chega a esses humores, mas a propriedade descreve o texto, não a regra de exibição.

**Deixado como está:** a pausa de descanso continua tomando o slot mesmo com o estado nomeando um
compromisso. São 15 minutos duas vezes ao dia, e ela é acionável *agora* enquanto a reunião é daqui
a horas — suprimi-la aí seria desligar a feature no momento em que ela serve.

### Emenda ao D-022 — a ordem da frase é decidida pelo custo de truncar

Pedido do usuário logo depois: *"pra mim é importante aparecer o horário ou em quantas horas vai
ser o próximo evento"*. Duas coisas estavam erradas.

**Em reunião, o próximo compromisso não tinha hora nenhuma** — o texto era
`Ocupado · X, faltam 45 min → Y`, só o título. Agora leva a hora de relógio. Relógio, e não
contagem, porque a frase já tem um "faltam X" da reunião atual: dois números relativos na mesma
linha obrigam a descobrir qual conta para qual reunião.

**E o horário morria no truncamento.** Com uma pausa em curso disputando largura, a barra mostrava
`Livre · Treinamento do GWS: Trilha para lí…` — o título consumia o espaço e levava o horário
junto.

A causa é ordem de frase, não falta de espaço: informação **de tamanho fixo e alto valor** estava
depois de informação **longa e de valor variável**. Invertido, o corte passa a comer o fim do
título, que é a parte que menos custa:

```
antes:  Livre · Treinamento do GWS: Trilha para lí…
agora:  Livre · às 14:00 · Treinamento do GWS: Tril…
```

Aplicado aos três humores que nomeiam evento — `rótulo · quando · título`. Em reunião virou
`Ocupado · faltam 45 min · X → 17:40 Y`, e o estouro virou `Estourou · passou 12 min · X`.

**Perto usa contagem, longe usa relógio**, com a fronteira em uma hora. "em 12 min" se age sem
pensar; "em 3h30" obriga a somar para descobrir que é às 14:00 e onde isso cai no dia. A dica de
ferramenta traz sempre a outra metade, para não desperdiçar o único lugar com espaço sobrando.

---

## D-023 — Um slot por pergunta: status, cronograma e pausa

**Status:** Aceita · 2026-08-18 · **corrige o D-019 e reestrutura o D-022**

**Contexto.** Relato de uso, três problemas na mesma tela.

**1. A pausa perseguia o usuário o dia inteiro.** *"Sempre tô com pausa pra fazer."* O
`BreakPlanner` descartava candidatas já terminadas, então quando a pausa ideal passava sem ser
tirada, ele escolhia a próxima janela livre — e depois a próxima. A folga reagendava para frente a
cada rodada de sync e nunca terminava de acontecer.

O D-019 afirma ter resolvido exatamente isso, e resolveu metade: a pausa *em curso* parou de fugir,
a *perdida* continuou correndo. A mesma doença, um estágio depois.

**2. Uma frase fazendo três trabalhos.** `Livre · às 14:00 · Treinamento do GWS…` misturava o
estado, o horário e o título num texto só, e o estado — a resposta que o produto existe para dar —
competia por largura com o nome de uma reunião.

**3. A pausa não tinha casa.** Ela tomava emprestado o slot do lookahead, então aparecer significava
sumir com outra coisa.

**Decisão.**

**A pausa é do período, não do instante.** `Find` não olha mais o relógio: escolhe a janela livre
mais próxima do meio do período e fica lá o dia todo, mudando só se marcarem reunião em cima.
Perdeu, perdeu — não vira cobrança.

**Três slots, três perguntas:**

| Slot | Responde | Largura |
|------|----------|---------|
| Status | "como estou agora?" | própria, nunca cede |
| Cronograma | "o que vem, e quando?" | o que sobra — é quem trunca |
| Pausa | "tenho folga marcada?" | própria, **colapsa** se desligada |

O status ganhou `TimeStatus.Label` separado de `TimeStatus.Summary`. Quem não usa pausas não paga
largura por elas: o slot some por completo.

### O fundo do status, e por que não é sempre colorido

O usuário pediu fundo próprio para destacar o estado. O caminho óbvio — acender o bloco na cor do
humor sempre — foi recusado: verde, azul, âmbar e vermelho viram todos blocos coloridos, e a
distinção entre "Em breve" e "Encerrando" passa a depender só de ler a palavra. O D-012 decidiu que
**a escalada acontece na forma**, e isso vale mais que a uniformidade.

A solução tem os dois: bloco com fundo **neutro** nos humores calmos, bloco **aceso** na cor do
humor nos que escalam. O status é sempre um bloco destacado, e calmo-vs-urgente continua legível
sem ler.

`ChipBackground` é o próprio primeiro plano da barra a 15% de opacidade, e não uma cor nova — segue
o tema claro/escuro de graça e não gasta vocabulário (regra 1).

### Como o app sabe se a pausa foi tirada: não sabe

Pergunta do usuário, e a resposta é o D-006 aplicado a outro caso: **sem detecção de presença, de
microfone ou de janela.** O Tempus conhece o cronograma; quem comunica é o usuário, clicando.

Duas consequências:

- **Ele nunca afirma que a pausa não foi tirada.** Pausa perdida apaga do slot, não alarma. Afirmar
  o contrário seria mentir com confiança sobre algo que ele não tem como saber (regra 10) — e
  viraria a cobrança que originou este relato.
- **Clicar no slot diz "tirei essa"**, o mesmo gesto de "eu vi" do resto do produto. O slot apaga
  pelo resto do dia. É opcional: não clicar não gera cobrança nenhuma.

O estado do dia virou `break-state.json` — data, dispensa e períodos tirados. A data continua sendo
o registro, pelo motivo do D-019: booleano exigiria limpeza na virada do dia, e um dia sem limpeza
seria um dia sem pausa.

**Consequências.** Verificado com a agenda real: `[Livre]` em bloco cinza, `às 14:00 · Treinamento
do GWS: Trilha para líde…` truncando no lugar certo, `☕ 15:00` no slot próprio, e o clique gravando
`Taken: ["Afternoon"]` e apagando o slot para `☕ tirada`.

**Não verificado:** o bloco aceso dos humores que escalam, que depende de estar perto de uma
reunião de verdade.

---

## D-024 — Excluir tarefa: dois cliques, e alvo grande o bastante para não se errar

**Status:** Aceita · 2026-08-18

**Contexto.** Pedido de um ✕ para excluir tarefa no painel S2. Excluir no Google Tasks **não tem
volta**: a API não expõe lixeira nem restauração.

**Decisão.** O ✕ aparece só com o ponteiro na linha — treze tarefas com um ✕ permanente cada viram
uma coluna de ruído ao lado do que importa, e a ação principal ali é concluir, não excluir.

O primeiro clique **arma**: o ✕ vira "Excluir?" em vermelho. Só o segundo apaga. Tirar o ponteiro
da linha desarma — quem se afastou não quis. Sem diálogo modal: a confirmação mora na própria
linha, onde o olho já está.

### O incidente que definiu o tamanho do alvo

A primeira versão tinha o ✕ com ~25px de largura. Ao verificá-la, o clique de teste caiu **15
pixels ao lado** e acertou a linha — que conclui a tarefa. Uma tarefa real do usuário saiu da lista
de abertas.

Recuperada por completo: concluir é reversível, ao contrário de excluir, e um `Patch` de volta para
`needsAction` devolveu a tarefa intacta. Mas o erro expôs o problema de desenho.

**Dois alvos vizinhos com efeitos diferentes, e o menor deles cercado pelo maior.** Errar por
quinze pixels trocava "vou excluir isto?" por "concluído". Não adianta a confirmação de dois
cliques se o caminho para chegar nela é mais estreito que a margem de erro da mão.

O alvo passou para **44×26px**, com o glifo centralizado. A regra que fica: *quando alvos vizinhos
têm efeitos diferentes e pelo menos um é irreversível, o menor precisa ser grande o bastante para
não se errar* — a confirmação protege do clique deliberado, não do clique torto.

**Consequências.** `GoogleSync.DeleteTaskAsync` documenta no próprio resumo que quem chama já
confirmou. O modo demo ganhou `DeleteTask` para a verificação não depender da conta real — o que
teria evitado o incidente, e é o caminho a usar daqui em diante para exercitar escrita.

### Emenda ao D-019 — adiar é diferente de dispensar

Pedido em uso: *"vai ter horas que eu vou ter que adiar o descanso"*. Os dois gestos que existiam
não cobriam isso — "tirei essa" mente, e "hoje não quero" joga fora a folga inteira por causa de
uma hora ruim.

**"Adiar pausa em 30 min"** grava um piso para o período; o planejador acha a próxima janela livre
a partir dali. Repetível: cada clique empurra mais 30. Fica no menu ao lado de "hoje não quero",
mas separado dele de propósito — um dia corrido não deve custar a folga inteira quando bastava
empurrá-la.

Depois de adiada, o alvo deixa de ser o meio do período e passa a ser **o quanto antes a partir do
piso**: quem adiou não quer o horário ideal, quer a folga assim que der.

**Dois erros que a verificação pegou:**

O piso era `agora + 30`. Adiar às 15:58 uma pausa marcada para 17:50 a puxava para **16:30** —
antecipar, não adiar. O piso passou a contar a partir do que vier mais tarde entre a hora atual e a
própria pausa, o que torna o gesto monotônico: 17:50 → 18:20 → 18:50 → 19:20.

E o menu oferecia "Adiar" mesmo com as duas pausas do dia já vencidas, quando o clique não fazia
nada. Agora ele só aparece havendo pausa por vir — prometer uma ação que não faz nada é pior que
não oferecê-la.

### Emenda ao D-023 — a folga é sugestão, e o usuário manda nela

Formulado pelo usuário em 2026-08-18: *"a folga é uma sugestão, pode ou não acontecer naquele
horário ou em um horário inesperado."*

Isso expôs uma consequência não intencional do D-023. Tornar a pausa **do período e não do
instante** matou a perseguição — mas também tornou a folga perdida **inalcançável**: às 16h, com as
duas pausas do dia vencidas, o slot colapsava e não sobrava gesto nenhum. O usuário queria descansar
e o app não tinha como registrar.

**"Tirar pausa agora"** fixa a pausa do período na hora atual, ignorando o meio do período e a
agenda. Sempre disponível com a funcionalidade ligada, inclusive com as duas do dia vencidas — é
exatamente aí que ela serve. Desfaz o "já tirei" e qualquer adiamento do mesmo período: os três
gestos falam do mesmo descanso, e o último a ser dado é o que vale.

Os quatro gestos, agora completos:

| Gesto | Diz | Onde |
|-------|-----|------|
| Tirar agora | "estou descansando" | menu |
| Adiar 30 min | "agora não, mais tarde" | menu |
| Tirei essa | "já descansei" | clique no slot |
| Hoje não quero | "hoje não" | menu |

**Sobre a call que cai em cima da folga**, também levantado: já funcionava e agora está registrado.
O planejador recalcula da agenda a cada sync e descarta janelas ocupadas, então uma reunião longa
sobre o horário ideal empurra a pausa para a primeira janela livre depois dela — verificado em uso
no mesmo dia, com um treinamento de 14:00–15:00 movendo a pausa da tarde para 15:00–15:15.

---

## D-025 — A escalada do nível 3 não guarda estado

**Status:** Aceita · 2026-08-18 · fecha a regra 2 do `CLAUDE.md`

**Contexto.** A regra 2 do projeto promete: *"Nível 3 escala, não decai — vermelho sólido, piscando
âmbar↔vermelho após 5 min, até o usuário clicar."* A metade do reconhecimento saiu no §10; faltava
a escalada. `ShellState.IsEscalated` existia e ninguém o ligava.

**Decisão.** `TimeStatus.IsEscalated`, calculado no resolvedor.

**Sem timer e sem memória.** O alarme de estouro começa no **fim marcado da reunião**, que está na
agenda. Então "há quanto tempo isto escala" é `agora − fim`, uma subtração — não um contador que
alguém precisa iniciar, parar e reiniciar quando o app reinicia. O resolvedor continua função pura
de (agenda, agora, opções) e a escalada sobrevive a restart de graça.

**Só o `Overrun` escala, e não por caso especial.** Entre os humores preenchidos, `Imminent` acaba
quando a reunião começa e `EndingSoon` quando ela termina — nenhum dos dois dura cinco minutos. A
janela de estouro é de dez. O limiar seleciona o caso certo sozinho; uma condição escrita à mão
para "se for Overrun" seria redundante e envelheceria mal.

**A configuração pode afrouxar, nunca antecipar.** `EscalationMinutes` passa por
`Math.Max(5, ...)`. A I8 fixa o piso: quem quiser esperar mais que cinco minutos pode, quem quiser
piscar antes não — a invariante não é negociável por arquivo de configuração.

### Onde ele pisca, e por que isso emendou o §0.5

O §0.5 dizia que piscar era do chip de motivo, e que o vermelho do slot de tempo *se resolve
sozinho*. Isso era verdade quando o slot só abrigava `Imminent`. `Overrun` quebrou a premissa.

Pôr o estouro **também** no chip para poder piscar lá violaria a I1 — o mesmo fato ocupando as
duas áreas coloridas. Então o piscar foi para onde o alarme já está: o bloco de status (D-023). O
critério deixa de ser o slot e passa a ser o fato — **escala o alarme que não se resolve sozinho.**

### Verificação, e um achado sobre esta máquina

A lógica do momento tem teste (I8: sólido até 4 min, escalado a partir de 5, configuração não
antecipa, período ≥ 1s, reconhecer encerra a escalada).

A **animação** foi verificada à parte, amostrando a cor do mesmo pixel doze vezes ao longo de 2,5s:
doze valores distintos oscilando entre `C02626` e `B4530A`, com os intermediários da interpolação.
Teste unitário não pega fiação de `SolidColorBrush`; esta prova pega.

**Achado, e o que ele produziu.** Na primeira medição o pixel devolveu `C02626` doze vezes
seguidas: sólido. A causa não era bug — `SystemParameters.ClientAreaAnimation` estava **False**,
porque as animações do Windows estavam desligadas nesta máquina, e a I8 manda ficar sólido nesse
caso. O código fez exatamente o que devia.

A prova da animação exigiu furar a guarda num build temporário. **Em seguida o usuário ligou as
animações no Windows**, e a medição foi refeita pelo caminho real, com a guarda no lugar: onze
valores distintos entre `C02626` e `B4520C`. A escalada está verificada como é entregue, na
configuração real da máquina.

Fica registrado que o caminho sólido **também** é comportamento válido e testado: quem desliga
animação costuma ter motivo — enjoo, epilepsia fotossensível, preferência — e o vermelho sólido já
comunica o essencial. A escalada perde a forma, não o recado.

---

## D-026 — Sinais, arbitragem e histerese: o chip finalmente acende

**Status:** Aceita · 2026-08-18 · implementa `SEVERITY.md` §2 e §4, fecha I1, I4 e I5

**Contexto.** O chip de motivo existia desde a Fase 2 e **nunca teve conteúdo no caminho real** —
`BuildState` devolvia `Severity.Calm` com motivo vazio. Toda a cor que o usuário via vinha do humor
temporal (§1.5). A escala 0–3, que é o assunto do documento mais importante do projeto, era letra
morta no código.

Arbitragem não podia ser feita antes porque não havia o que arbitrar.

**Decisão.** Três camadas separadas, cada uma testável sozinha:

| Camada | Responsabilidade | Estado |
|--------|------------------|--------|
| `Signals` | avaliar os onze sinais do §2 | nenhum — função pura |
| `Arbiter` | escolher um vencedor (§4) | nenhum — função pura |
| `SeverityGate` | suavizar a descida (I4, I5) | mínimo, e justificado |

Separá-las não foi enfeite: cada uma falha de um jeito diferente, e juntas seriam intestáveis.

### A única classe do domínio que guarda estado

`SeverityGate` guarda o nível exibido e desde quando. Isso contraria a regra 8 de propósito, e a
justificativa é que **histerese é sobre "quanto tempo faz"**, e isso não sai de (dados, agora) — a
mesma entrada tem que produzir saídas diferentes conforme o que veio antes. É a definição de
estado.

O estado é mínimo (dois campos) e a classe é determinística: mesma sequência de entradas, mesma
saída. Nada de relógio interno, nada de I/O.

**Subir é imediato, descer espera 20s.** Atrasar um alarme para não tremer trocaria o problema
certo pelo errado: tremer incomoda, chegar tarde custa. E trocar de motivo dentro do mesmo nível
**não reinicia** o relógio — senão a barra ficaria presa num nível enquanto sinais se revezassem
nele.

Reconhecimento escapa da histerese, como a própria I4 diz. Quem clicou "eu vi" e continuou vendo
vermelho por vinte segundos concluiria que o gesto não funciona.

### `Severity` mudou de casa

Estava em `Tempus.Shell`. É o vocabulário do **modelo**, não da tela: os sinais produzem
severidade, a arbitragem escolhe uma, e só então a barra pinta. Enquanto morou na shell, nada disso
podia existir sem o domínio depender da UI — o inverso da regra 8. Foi o compilador que apontou,
recusando `Severity` dentro de `Tempus.Domain`.

### Detalhes que o §2 pede e são fáceis de perder

**`SelfClearing`.** `MeetingEnded`, meio-dia limpo e "dia fechado" passam sozinhos. Eles não
oferecem o gesto de reconhecer: propor uma ação para algo que já vai sumir gasta atenção sem dar
poder nenhum.

**Reconhecer cala os dois vocabulários.** O humor temporal e o sinal podem estar acesos ao mesmo
tempo (§0.5) — são áreas diferentes com gramáticas diferentes. Um clique suprime as duas
ocorrências, senão o gesto resolveria metade do que o usuário vê.

**O chip escala pelo mesmo truque do D-025.** `Signal.Since` é derivado: `MeetingRanIntoNext` nasce
no fim marcado da reunião e `DayEnded` às 17:00. Nenhum relógio por sinal, e a escalada sobrevive a
restart.

**Consequências.** Verificado com a agenda e as tarefas reais às 18:35: o `DayEnded` acendeu o chip
em vermelho com *"Jornada encerrada: 2 tarefas abertas"*, e o pixel do fundo do chip oscilou entre
`C02626` e `B4520C` — piscando, porque já passava muito dos 5 min.

**Mudança de comportamento que o usuário vai notar:** a partir das 17:00, todo dia em que sobrar
tarefa aberta, a barra fica vermelha e passa a piscar cinco minutos depois, até um clique. É
exatamente o §2.2, mas é a primeira vez que acontece de verdade.

---

## D-027 — Evento ativo e contagem do fim de jornada: a Fase 3 fecha

**Status:** Aceita · 2026-08-18 · implementa `SEVERITY.md` §8 e o resto do D-007

### O evento ativo (§8)

Duas reuniões aceitas no mesmo horário. O Tempus não sabe em qual você está — não há detecção de
presença (D-006) — e adivinhar produz o pior ruído possível: um `MeetingRanIntoNext` **falso** no
fim da primeira, que é justamente o alarme mais caro do produto.

`ActiveEvent` resolve em duas camadas. **Pergunta**, com um menu curto no chip; e **enquanto não há
resposta**, assume um padrão determinístico — `accepted` > `tentative` > `needsAction`, desempatando
pelo início e depois pelo fim. A barra nunca fica sem estado esperando um clique que pode não vir.

O que faz a regra valer é uma linha só: reuniões sobrepostas à ativa **saem do ciclo** do §2.1 até
terminarem. Não é filtro de exibição — elas deixam de existir para os sinais.

**A escolha vive em memória, de propósito.** Ela vale pela janela de sobreposição, que dura
minutos; perdê-la num restart degrada para o padrão determinístico, que já é uma resposta razoável.
Persistir custaria um terceiro arquivo de estado por um ganho que dura o intervalo entre duas
reuniões.

**Dois testes antigos quebraram, e isso foi a prova.** Eles usavam uma "próxima" reunião começando
14:55 contra uma que terminava 15:00 — cinco minutos de sobreposição. Sob o §8 isso é reunião
paralela e o vermelho é corretamente suprimido. Os testes codificavam exatamente o falso positivo
que o §8 foi escrito para eliminar; corrigi-los para reuniões de fato sequenciais foi a confirmação
de que a regra pega o caso certo sem desligar o alarme legítimo.

### `DayEnded.CountMode` (D-007)

`AllOpen` (padrão) conta toda tarefa não concluída; `DueTodayOrOverdue` conta só o que vencia hoje
ou antes. O D-007 previu o arrependimento e reservou a chave: se o vermelho das 17:00 incomodar
depois de uma semana de convívio, é uma linha de `appsettings.json`.

O checkpoint do meio-dia usa o mesmo critério — contar diferente nas duas pontas do dia seria uma
inconsistência que ninguém lembraria de explicar depois.

**Consequências.** A Fase 3 fica com um item: os toasts (§5), que exigem AUMID registrado e mudança
de *target framework* para acessar as APIs de notificação do Windows. É trabalho de natureza
diferente do que veio até aqui, e merece decisão própria.

---

## D-028 — Toasts: só o vermelho tem licença para interromper

**Status:** Aceita · 2026-08-19 · implementa `SEVERITY.md` §5 (parcialmente, de propósito)

### O escopo, que é menor que o §5

O §5 prevê quatro coisas: toast ao entrar em `Offline`, toast em três transições de âmbar, e duas
interrupções por nível 3. **Só as duas últimas foram implementadas.**

O corte não é falta de tempo. Cor é ambiente — você olha quando quer; toast é interrupção — ele te
acha. O orçamento de interrupção é muito menor que o de cor, e a regra 1 já diz que se o vermelho
aparecer mais de ~3× num dia normal o modelo está errado. Começar pelos dois sinais que **nunca se
limpam sozinhos** (`MeetingRanIntoNext` e `DayEnded`) dá uma ou duas interrupções por dia. Os
âmbares escolhidos pelo §5 dariam uma dúzia, e boa parte delas duplicaria o lembrete que o próprio
Google Calendar já dispara.

Subir o escopo depois é acrescentar casos em `ToastPolicy`. Descer, depois de acostumar o usuário
a ser interrompido, é bem mais caro — e o desacostumar acontece do jeito ruim, com ele desligando
tudo.

O toast de `Offline` continua sendo a ideia mais defensável que ficou de fora, e está anotado no
`ROADMAP.md`: uma vez por semana, quando o *refresh token* expira (D-003).

### O toast é anúncio, não ação

**Sem botões e sem tratador de clique.** Um app não empacotado — que é o nosso caso desde o D-001 —
só recebe ativação de toast registrando um servidor COM no registro do Windows, com o
desregistro correspondente na desinstalação. É bastante máquina para um app de um usuário só, e ela
existiria para duplicar um gesto que já funciona: o clique na barra.

Então o corpo do toast **diz em voz alta** onde está o gesto: "Clique na barra para dispensar".
Sem isso o usuário procuraria o botão que não existe.

O reconhecimento na barra também **retira o aviso da Central de Ações**. Deixar lá um vermelho já
resolvido é mostrar dado velho com cara de atual — a regra 10 vale para a notificação, não só para
os contadores.

### Supressão é retenção, não descarte

Em apresentação, tela cheia ou D3D exclusivo, nada sai — e **nada é marcado como enviado**.
Terminada a apresentação, se o vermelho ainda estiver de pé, a interrupção acontece na volta.

Descartar seria perder justamente o aviso mais valioso: apresentação é quando é mais fácil estourar
o horário e não perceber.

Consequência: depois de uma apresentação longa, a entrada e a escalada podem estar vencidas ao
mesmo tempo. Nesse caso sai **só a escalada**, e a entrada é consumida em silêncio. Duas
interrupções em sequência não são dois avisos; são um susto.

### A deduplicação vive em memória

Chave `(ocorrência, tipo)`, com a ocorrência do §7 — que já carrega evento, início e fim, então
remarcar uma reunião produz chave nova, e remarcar é de fato uma situação nova.

O conjunto **não é persistido**, ao contrário dos reconhecimentos. Um restart com o vermelho ainda
de pé reemite o aviso, e isso é o comportamento certo: a situação continua sem resolução, e o
momento logo após um restart é exatamente quando se perdeu o contexto do que estava aberto.

### AUMID: a parte que falha em silêncio

App não empacotado só notifica se duas coisas concordarem: o processo declara um
*AppUserModelID*, e existe um atalho no Menu Iniciar com o mesmo ID na propriedade
`System.AppUserModel.ID`. Sem o atalho a chamada **não falha** — ela simplesmente não mostra nada.

O `install.ps1` cria o atalho via `WScript.Shell`, que não sabe gravar propriedades. Em vez de
reescrever o instalador com ~120 linhas de interop em PowerShell, o app **conserta o atalho
existente** na subida: carrega o `.lnk`, acrescenta a propriedade e preserva o alvo. Isso é o que
permite rodar o build de desenvolvimento sem sequestrar o atalho da instalação — o AUMID vale para
a máquina, não para o caminho do executável.

`InitPropVariantFromString` **não serve**: apesar de documentado, é uma função *inline* do
`propvarutil.h` e não um export de verdade; o P/Invoke morre com `EntryPointNotFoundException` em
tempo de execução. O `PROPVARIANT` é montado à mão — `VT_LPWSTR` e string em memória COM, que é o
que aquele helper faria.

### `--toast-probe`

Uma sonda permanente, não andaime de depuração. A falha típica deste caminho é silenciosa, e o app
roda em duas máquinas (`DEPLOY.md`): confirmar a entrega ao instalar, sem esperar um nível 3 de
verdade acontecer, vale as dez linhas. Ela diz **por que** falhou quando falha — um diagnóstico que
só diz "falhou" é meio diagnóstico.

### *Target framework*

`net8.0-windows` → `net8.0-windows10.0.19041.0`, pelas projeções WinRT de
`Windows.UI.Notifications`. Subiu junto no projeto de testes, que não pode ter alvo mais restrito
que o testado. Foi commitada **sozinha**, antes de qualquer código de toast, para ser revertível
por si — é a única mudança estrutural do lote e não toca uma linha de lógica.

**Consequências.** A Fase 3 fecha. O `ToastPolicy` é puro e testado como o resto do domínio
(regra 8); quem fala com o Windows é `ToastChannel`, que não decide nada.

---

## D-029 — Escrita otimista com reversão: o clique deixa de sumir

**Status:** Aceita · 2026-08-19 · primeira fatia da Fase 4

### O defeito

As três escritas que existiam — concluir, criar, excluir — eram *fire-and-forget* com falha
silenciosa: `catch (Exception) { return false; }` no `GoogleSync`, e o resultado descartado com
`_ =` por quem chamava. Falhou? Nada acontece e ninguém fica sabendo.

Três fatos tornavam isso concreto:

1. **A janela de perigo dura dez minutos.** `IsUsable` aceita `Failing` desde que o último sync
   tenha menos de 10 min. Com a rede caída a barra continua viva, o painel continua clicável, e
   toda escrita nesse intervalo se perdia.
2. **Criar era perda de dado de verdade.** A caixa de texto era limpa *antes* de o evento
   disparar. O título digitado sumia da tela mesmo quando não virava tarefa nenhuma, e não havia
   como reconstruí-lo.
3. **A tela só mudava depois da ida e volta**, porque cada escrita disparava um `PollAsync`
   completo — calendário, tarefas e Gmail — só para a linha sumir.

### A intenção vira dado

`PendingWrite` registra o que o usuário mandou fazer. É o que permite mostrar antes de confirmar,
repetir depois, e sobreviver a um restart. `TaskProjection.Apply(servidor, pendentes)` é o que a
tela mostra — função pura, como o resto do domínio (regra 8).

**A regra que resume tudo:** o efeito otimista vale *enquanto* a escrita está pendente. Assim que
ela falha, a linha volta à verdade do servidor e carrega o aviso. **A reversão não tem código
próprio** — cai da mesma função.

Isso **não fere a regra 10** ("nunca mostrar dado velho como se fosse atual"). Pendente não é dado
velho: é o dado mais novo que existe, produzido pelo usuário há um instante. O que a regra proíbe
é apresentar o passado como presente, e é justamente isso que a reversão evita.

### A projeção é central, não só do painel

A barra conta pela mesma projeção que o painel desenha. Sem isso o chip diria "2 tarefas abertas"
enquanto a lista logo acima mostra uma — e duas superfícies que discordam custam mais confiança
que a soma dos dois erros separados.

### "Google ganha empate" cai de graça

`TaskProjection.IsConfirmed` compara a intenção com o retrato do servidor. Confirmada, a intenção
**deixa de existir** e o servidor volta a ser a única verdade. Não foi preciso escrever regra de
conflito: ela é consequência de a intenção ser temporária por construção.

O mesmo mecanismo impede **duplicata depois de um restart**, quando a escrita deu certo mas a
resposta se perdeu. Para criação, sem timestamp no retrato, a comparação é por título aberto e
igual — erra para o lado de não duplicar, que é o lado certo: uma criação engolida se refaz com um
clique, e uma tarefa duplicada incomoda até alguém apagar na mão.

### Repetição: três tentativas, e 403 não é permanente

`WritePolicy` decide, `WriteQueue` executa — mesma divisão do D-028. Backoff de 2 s, 8 s, 30 s;
depois de três tentativas vira "não salvou" e a decisão passa a ser do usuário.

Permanente é só **400, 404 e 410**. O **403 fica de fora de propósito**: o Google usa 403 tanto
para permissão negada quanto para `rateLimitExceeded`, e tratá-lo como permanente descartaria
escrita legítima numa rajada. Permissão negada apenas gasta três tentativas antes de virar "não
salvou", que é um final honesto.

`Classify` recebe o **código HTTP**, não a exceção do SDK: com a exceção, o domínio passaria a
depender do cliente HTTP e deixaria de ser testável sem ele.

### A fila é persistida

`%APPDATA%\Tempus\pending-writes.json`. O caso que dói é o que atravessa o fechamento do app: você
digita uma tarefa com a rede caída e fecha o notebook. Concluir e excluir se refazem olhando a
lista; um título digitado, não.

Ao subir, `Pending` volta a tentar com tentativas zeradas — o app caiu, mas a rede pode ter
voltado. `Failed` continua esperando o clique. Fila vazia **apaga** o arquivo em vez de gravar
`[]`: o estado normal é não dever nada, e a ausência torna isso óbvio para quem olhar a pasta.

### O painel

Pendente é **esmaecida** — sem cor nova, só menos presença. Falhada ganha a pílula `não salvou`,
na cor crítica que as vencidas já usam, porque quer dizer a mesma coisa: isto precisa de você. O
motivo exato fica no tooltip.

Numa linha falhada o ✕ significa **descartar a alteração**, não apagar a tarefa, e a palavra da
confirmação muda junto — "Descartar?" em vez de "Excluir?". Um glifo com dois significados só é
honesto se disser qual está em jogo na hora de decidir.

Efeito colateral bem-vindo: limpar a caixa ao criar passou a ser honesto, porque a linha
provisória aparece na hora. O defeito 2 morreu sem tratamento próprio.

### `--demo --fail-writes`

O modo demo passa pela **mesma fila** e chega em `FakeStateSource.ApplyAsync`. Se fosse um atalho,
a regra do D-024 — verificar escrita em demo, nunca na conta real — não provaria nada sobre o
código que roda de verdade. `--fail-writes` faz toda escrita falhar, para exercitar o caminho que
não dá para provocar sob demanda.

Fila própria, com arquivo próprio (`pending-writes.demo.json`): escrita falsa não pode entrar na
fila que vai subir para a conta de verdade.

### O defeito que só o teste visual pegou

`Discard` gravava sem avisar: a intenção saía da fila e a linha continuava na tela até algo não
relacionado forçar um desenho. Os testes unitários não viam porque não observam o evento. Agora
**toda** mudança passa por um `Announce()` que grava e avisa juntos — e há teste de regressão
para os quatro caminhos.

É a mesma lição do D-025 e do D-028: teste de unidade prova a decisão, não a fiação.

### O que ficou de fora

**Editar título e vencimento** é a próxima fatia, e fica barata: dois valores a mais no
`WriteKind` e a UI de edição. Foi decidido com o usuário fazer confiabilidade primeiro —
acrescentar duas escritas novas sobre um mecanismo que perde escrita seria construir sobre o
problema.

**Escrita falhada não vira sinal de severidade.** Com o painel fechado ela é invisível, e isso
pede um sinal na barra — mas sinal novo é cor nova, que exige entrada na tabela do `SEVERITY.md` e
revisão de I1–I8 (regra 1). Virou **Q-02** em `SEVERITY.md` §9 em vez de virar suposição no código.

---

## D-030 — Desfazer conclusão: a metade que faltava do clique único

**Status:** Aceita · 2026-08-19 · encontrada em uso

### O buraco

O usuário marcou uma tarefa como concluída sem querer e **não tinha como desfazer**. A tarefa
some da lista no instante do clique e o painel não conhece concluídas — a única saída era abrir o
Google Tasks no navegador.

Isso não é só uma funcionalidade ausente: é uma **incoerência do próprio modelo de gesto**. O
D-024 deu dois cliques ao ✕ porque excluir não tem volta. Concluir ficou com um clique só, ao lado
do ✕, sob a premissa de que é reversível. **A premissa era falsa** — não havia volta. Ou concluir
encarecia para dois cliques, ou ganhava a volta. Encarecer o gesto mais frequente do painel para
proteger contra um erro raro é o negócio errado; então ganhou a volta.

### Duas requisições, não uma

`showCompleted` na mesma consulta seria mais curto e está errado por dois motivos:

- **`MaxResults` é compartilhado.** Um monte de concluídas empurraria tarefas abertas para fora do
  retrato. Perder aberta para mostrar concluída é o pior negócio possível aqui.
- **`completedMin` filtra por data de conclusão**, e é plausível que exclua quem não tem nenhuma —
  que é toda tarefa aberta. Nenhuma documentação garante o contrário.

Separadas, cada consulta tem um trabalho e as duas dúvidas somem. A segunda ainda é filtrada no
cliente para `IsCompleted`, porque ela pode devolver abertas e duplicá-las contaria a mesma tarefa
duas vezes no chip da barra.

Custo: dobra as chamadas de `tasks.list` por lista. Com uma ou duas listas e cadência de 15 s
ativos, é irrelevante perto da quota — e a alternativa era um retrato que às vezes mente.

`showHidden` junto com `showCompleted`: no Google Tasks concluir **também esconde**, e sem os dois
a tarefa não volta.

### Janela de sete dias

`Sync.CompletedDays`, padrão 7. Cobre "marquei sem querer" e "mudei de ideia na semana". Ilimitado
só faria a lista crescer com coisa que ninguém vai reabrir, e ainda por cima competindo por
`MaxResults`.

### O gesto é simétrico

Clicar numa linha aberta marca; clicar numa concluída desmarca. **O mesmo gesto nos dois sentidos**
torna a volta óbvia sem precisar de botão nomeado — quem descobriu como concluir já sabe como
desfazer.

A seção nasce **recolhida**, uma linha só (`▸ Concluídas · 2`). O painel responde "o que está
aberto", e concluída não é resposta para isso; mas precisa estar ao alcance de um clique, senão o
buraco continua. O estado de expansão mora no painel e sobrevive aos redesenhos — um sync não pode
recolher a seção embaixo do clique do usuário —, mas **não** é persistido: cada abertura volta a
responder a pergunta principal.

Ordenadas da **mais recente para a mais antiga**: a marcada sem querer é a última, e fica no topo,
exatamente onde o clique que a desfaz precisa alcançar. É por isso que `TaskItem` ganhou
`CompletedAt`, e por isso ele converte para o fuso local — ao contrário do vencimento, aqui a hora
importa.

### Assimetria na confirmação

Para `Complete`, sumir do retrato **confirma** (a leitura de abertas não traz concluídas). Para
`Reopen`, sumir **não** confirma: a tarefa pode ter caído da janela de sete dias em vez de ter
voltado a aberta, e confirmar aí seria dizer "pronto" sobre algo que talvez não aconteceu. `Reopen`
só confirma vendo a tarefa aberta no retrato.

### Custou pouco por causa do D-029

`Reopen` é um valor a mais no `WriteKind`, um `case` na projeção e um `case` no executor. Ganha de
graça a resposta imediata, a repetição automática, a reversão e a persistência. Era a aposta feita
ao fazer confiabilidade antes de funcionalidade, e ela se pagou na primeira vez que foi cobrada.

---

## D-031 — O campo `due` da API não é o "prazo" da interface do Google

**Status:** Aceita · 2026-08-19 · encontrada em uso, diagnosticada com `--dump-tasks`

### O sintoma

"Coloquei data nas tarefas no Google Tasks e não refletiu no Tempus."

### O diagnóstico

Quatro medições, nesta ordem, cada uma descartando um culpado:

1. **JSON cru vindo do `tasks.list`** — sem campo `due`. O `updated` da tarefa **tinha** mudado, então a edição chegou ao Google; a data, não.
2. **`tasks.get` na mesma tarefa** — resposta byte a byte idêntica, mesmo etag. Não era a chamada de lista.
3. **Escrita pela API** com `due = 2026-08-20T00:00:00.000Z` — devolvida na criação, relida no `get`, e o parse do Tempus converteu certo. **O campo funciona nos dois sentidos.**
4. **O usuário olhou a interface**: a tarefa escrita pela API aparece como "**Amanhã**"; as que ele datou pela interface aparecem como "**expira amanhã**". Rótulos diferentes, campos diferentes.

### A causa

O Google lançou em **novembro de 2025** um campo de **prazo** (*deadline*), separado da data de vencimento clássica. Ele **não está na API do Tasks**. O botão "Adicionar prazo" da interface escreve nele; o `due` que a API expõe é o outro.

Não é o velho descompasso entre o campo da API e o que o Calendar usa — é um terceiro conceito, novo, e sem cobertura de API nenhuma.

**A confirmação veio da documentação, não de notícia.** Na revisão `rev20251102` o Google **reescreveu a descrição do `due`**:

> *Scheduled date for the task (as an RFC 3339 timestamp). Optional. This represents the day that the task should be done, or that the task is visible on the calendar grid.* **It doesn't represent the deadline of the task.**

E a lista de campos dessa mesma revisão **não tem `deadline`**. O Google renomeou conceitualmente o `due` para "data agendada", declarou explicitamente que ele não é o prazo, e não expôs o prazo. **Atualizar a biblioteca cliente não resolve** — o campo não existe no contrato.

**Consequência de vocabulário:** o que o painel chama de "Vencidas" é, no contrato novo, "agendada para um dia que já passou". Continua sendo a leitura útil no dia a dia, mas não é mais a mesma coisa que o "prazo" que o usuário vê no Google — e é por isso que os dois podem discordar sem que nenhum esteja errado.

### Erro de método que quase passou

A primeira "prova" foi um JSON que eu chamei de cru e **não era**: era a re-serialização do objeto já convertido pela biblioteca, que **descarta em silêncio** qualquer campo que não conheça — e a nossa é anterior ao lançamento do prazo. A conclusão sobreviveu porque o contrato mais novo também não tem o campo, mas o método estava errado e podia ter mentido.

Para inspecionar resposta de API, ler o **corpo HTTP**, nunca o objeto desserializado.

### O que isso decide

- **`due` continua sendo a fonte** do vencimento no Tempus. Ele é escrevível, legível e o parse está correto — as medições 3 e 4 provam os dois sentidos.
- **"Editar título e vencimento" continua na Fase 4.** Chegou a estar em risco: se o campo fosse morto, o editor nasceria quebrado. Não é o caso.
- **Não há workaround para o prazo novo.** Tarefas em que o usuário usa "Adicionar prazo" continuarão sem data para o Tempus até o Google expor o campo. Nada a fazer no código.
- **A regra prática, que se autoverifica:** se o Google Tasks escreve "**Amanhã**" ou "**21 de ago**", o Tempus enxerga; se escreve "**Expira amanhã**" ou "**Data de conclusão: …**" com o ícone de alvo, não enxerga. O rótulo é o teste, e não depende de lembrar qual botão foi usado.

### Onde fica cada campo na interface — confirmado em uso

O menu rápido de três pontos oferece **"Editar prazo"**, que escreve no campo novo, invisível para a API. **Abrir a tarefa** — clicar no título, não no ⋮ — dá acesso ao campo de data clássico, que escreve no `due`.

Confirmado pelo usuário em 2026-08-19: lançando por dentro da tarefa, as duas tarefas passaram a chegar com `due = 2026-08-21` e apareceram no painel.

O outro caminho para o mesmo campo é o **Google Agenda**: a documentação define `due` como "o dia em que a tarefa fica visível na grade do calendário", então criar ou arrastar a tarefa lá mexe nele.

É uma pegadinha de interface, não um defeito: o gesto mais à mão escreve no campo errado, e os dois ficam a um clique de distância um do outro.

### A sonda ficou

`--dump-tasks` transformou "não refletiu" em resposta definitiva em duas execuções, separando "o Google não mandou" de "o Tempus não desenhou". Mesma justificativa do `--toast-probe` (D-028): a falha desse caminho é silenciosa. A sonda de escrita que criou a tarefa descartável foi **removida** — respondeu à pergunta e não se repete.

**Sobre escrever na conta real:** a regra do D-024 diz para verificar escrita em demo. Aqui a pergunta só podia ser respondida contra a conta de verdade, então foi **perguntado antes**, feito com uma tarefa descartável em vez de uma real, e limpo em seguida.

---

## D-032 — Duas áreas coloridas nunca podem parecer a mesma coisa

**Status:** Aceita · 2026-08-20 · encontrada em uso · implementa a invariante I9

### O sintoma

*"Quando falta alguns minutos para uma call e estou iniciando uma call, fica tudo na mesma cor,
confunde um pouco."*

### A premissa que envelheceu

O §0.5 afirmava que a **forma** já separava os dois vocabulários: slot de tempo com texto tingido,
chip sempre preenchido. Isso era verdade quando foi escrito. Deixou de ser quando `EndingSoon`,
`Imminent` e `Overrun` passaram a **preencher** o slot — aí os dois viraram pílulas, e como
compartilham a paleta, viraram pílulas idênticas.

A paleta prova: `AttentionBackground` (`#B4530A`) tem **três** papéis simultâneos — preenchimento
do slot em `Encerrando`, cor de texto do slot em `Em breve` e `Dia Encerrado`, e preenchimento do
chip em severidade `Attention`. `CriticalBackground` (`#C02626`) idem entre `Estourou` e `Critical`.

### Família de matiz, não igualdade de cor

`ColorVocabulary.Collide` compara **famílias** (Green, Blue, Amber, Red), não valores. Por
igualdade, `Ocupado` em `#8FB8DC` ao lado de um chip `#1E4B73` passaria batido — e são justamente
os dois azuis que motivaram a queixa. O olho não compara hexadecimais.

Função pura, sem tocar em `System.Windows.Media` (regra 8): a decisão é do domínio, a pintura é da
paleta.

### Quem cede é o chip

Dois motivos. O slot está no ar o **dia inteiro**, e mudar a forma dele o tempo todo seria mais
perturbador que mudar a de algo que aparece raramente. E tirar o preenchimento do slot apagaria a
**escalada do `Overrun`** (D-025), que é o alarme mais caro do produto.

O contorno continua lendo como alarme, e o piscar do nível 3 passa a animar a **borda** quando o
chip está em contorno — animar o fundo transparente não mostraria nada. A I8 continua valendo:
muda onde o piscar acontece, não se acontece.

### A tinta do contorno é uma cor própria — e isso custou duas tentativas

**Primeira tentativa:** escolher por tema. No escuro a cor saturada, no claro a de texto. Errado —
o teste de contraste pegou: no tema **escuro** o azul de `Info` (`#1E4B73`) dá **1,81:1** contra a
barra `#1F1F1F`. Um contorno fantasma. O usuário reclamou disso no mesmo minuto em que o teste
falhou: *"o tom de azul é muito escuro pra opção com contorno, não dá pra ler direito"*.

**Segunda tentativa:** escolher por contraste, preferindo a saturada quando ela passa. Melhor, mas
o vermelho (`#C02626`, 2,76:1) também reprovava e caía para o `CriticalForeground` — um rosa
pálido que **perde a identidade de alarme**.

**O que valeu:** contorno é um **terceiro contexto**, e contexto novo pede cor nova. Entraram
`InfoInk`, `AttentionInk` e `CriticalInk` na paleta — no escuro claras o bastante para ler (todas
acima de 5:1) e ainda inconfundivelmente azul, âmbar e vermelha; no claro as escuras que já
existiam servem.

A lição: quando duas cores existentes não servem para um papel novo, o problema é achar que o papel
é velho.

### Verificação

`PaletteOutlineTests` mede **contraste WCAG** de cada tinta contra a barra nos dois temas, e o
matiz pelo canal dominante — um vermelho de alarme não pode virar rosa. O tema claro é o caminho
que ninguém exercita no dia a dia; quem tem de pegá-lo é o teste.

`ColorVocabularyTests` cobre os **32 pares** de humor × severidade, afirmando que não sobra nenhuma
combinação de mesma família com mesma forma.

E a prova visual no `--demo`, que alterna os humores sozinho: `Encerrando` preenchido ao lado de
"Daily começa em 1 min" em contorno, separáveis de relance; azul e vermelho mantendo o
preenchimento quando não colidem.

### O que ficou de fora

A redundância entre as duas áreas — elas frequentemente dizem o **mesmo fato** — virou **Q-03** no
`SEVERITY.md` §9. Foi apresentada ao usuário como alternativa mais profunda e ele escolheu só a
separação visual, por ser mudança menor. Registrada em vez de assumida.

---

## D-033 — O slot é o dono da narrativa de call; o chip é para o resto

**Status:** Aceita · 2026-08-20 · fecha a Q-03 do `SEVERITY.md` §9

### O que destravou

A Q-03 tinha sido aberta no D-032 e **recusada pelo usuário** no mesmo dia. O que a destravou não
foi mudar de ideia — foi descobrir o motivo da recusa:

> *"Eu só não queria perder o clique pra entrar na call se eu me atrasar 1 minuto... mas se tiver
> uma forma de tirar essa redundância e também manter a possibilidade de entrada rápida com um
> click e que fique claro que estou entrando, seria ótimo."*

**O receio não se sustentava.** A entrada nunca esteve no chip:

| Área | O clique faz |
|---|---|
| Pílula de estado | reconhece se há alarme; senão entra na call |
| **Texto do compromisso** | **entra na call, sempre** — nunca reconhece |
| Chip de motivo | reconhece · escolhe reunião · abre agenda — **nunca entra** |

A lição de processo vale mais que a técnica: a pergunta certa não era "cor ou redundância?", era
**"o que o usuário está protegendo?"**. Uma recusa costuma ter uma restrição embaixo, e ela quase
sempre é mais fácil de satisfazer do que a recusa sugere.

### Tabela explícita, não regra por categoria

`ChipEcho` lista quais sinais cada humor já conta:

| Humor | Já conta |
|---|---|
| `Approaching` | `MeetingUpcoming` |
| `Imminent` | `MeetingImminent` |
| `InMeeting` · `EndingSoon` | `MeetingStarted`, `MeetingBackToBack` |
| `Overrun` | `MeetingRanIntoNext`, `MeetingEnded` |

A regra "todo sinal de categoria `Call` é eco" pareceria mais elegante e **estaria errada**:
`MeetingAmbiguous` é de categoria `Call` e fala do mesmo evento, mas ali o chip está **fazendo uma
pergunta** que o slot não sabe fazer, e cujo clique abre o seletor do §8. Calá-lo quebraria o
gesto. Fora da tabela, nunca é suprimido — como `MiddayCheckpoint`, `DayEnded` e `TaskOverdue`.

### Só de pintura, e o teste que garante

O sinal calado continua vencendo a arbitragem, alimentando o toast e aceitando o reconhecimento.
Se a supressão mexesse na arbitragem, o vermelho das 17:00 poderia sumir sem ninguém ver — por isso
existe um teste que afirma exatamente isso: chip suprimido, `Arbiter.Winner` inalterado.

O caso se resolve sozinho quando deveria: passados os 10 min de `Overrun`, o humor troca de evento,
os `EventId` deixam de bater e o chip reaparece com o vermelho ainda não reconhecido.

### `EventId` explícito

A ocorrência é `humor|evento|início|fim`, e dava para extrair o evento por *substring*. Não foi
feito: funcionaria hoje e quebraria em silêncio no dia em que o formato mudasse. `TimeStatus` e
`Signal` ganharam `EventId` de verdade.

**Na dúvida, o chip fala.** Sem evento de algum dos lados não há supressão: perder um alarme é
caro, repetir uma informação não é.

### Deixar claro que ali se entra — antes e depois

*"Que fique claro que estou entrando"* tem duas leituras, e as duas eram lacunas:

- **Antes:** o texto do compromisso ganhou um **▶** quando há link. Sem cor — afordância não paga
  o orçamento da regra 1 — e o espaço sobra justamente porque o chip repetido calou. Sem link não
  há glifo, que é a distinção do D-022 finalmente visível em vez de escondida no tooltip.
- **Depois:** o clique passa a responder **"Abrindo a call…"** por 3 s. Abrir o navegador demora, e
  o gesto não devolvia nada. **O projeto já tinha resolvido isso uma vez**, no re-consent, pelo
  mesmo motivo.

De quebra, o tooltip daquele texto dizia "Clique para abrir a agenda" **mesmo quando o clique
entrava na call** — a dica contradizia o gesto. Corrigido.

### O modo demo não passava pelo `BuildState`

A supressão é decidida em `App.BuildState`, que o demo não usa: ele desenha estados roteirizados.
Sem intervenção, a tira de verificação **não exercitaria nada** desta decisão — e teria dado a
impressão de que exercitava.

O roteiro ganhou um estado com `ChipRepeatsTime`, e é ele que aparece na tira: severidade âmbar,
motivo preenchido, reconhecível, e nenhum chip desenhado. É a mesma lição do D-025, D-028, D-029 e
D-032 aparecendo pela quinta vez — **teste de unidade prova a decisão, não a fiação** —, agora com
um agravante novo: um caminho de verificação pode estar cego sem avisar.

---

## D-034 — Ler todos os calendários, não só o principal

**Status:** Aceita · 2026-08-20 · encontrada em uso

### O buraco

O usuário importou o calendário do Teams para o Google Agenda — um calendário assinado chamado
"Calendário", nome que a integração não deixa mudar. `_calendar.Events.List("primary")` lia só o
principal, então **nenhuma** reunião marcada no Teams chegava à barra. Prova concreta: a call
"Backlog Pearson", das 13:00, invisível.

Isso é pior que funcionalidade faltando. Enquanto a empresa migra de ferramenta (Azure/Teams →
Google), **parte da agenda vive do outro lado**, e uma barra que responde "algo precisa de mim
agora?" com metade do calendário dá falsa sensação de cobertura — o mesmo defeito que a regra 10
combate nos contadores.

### O principal sempre, mais os visíveis

Critério: o principal **sempre**, mais todo calendário com `selected == true` — os que o usuário
enxerga no próprio Google Agenda. Escondeu lá, some daqui, sem configuração.

**A armadilha do `selected`:** a documentação diz *"Optional. The default is False"*, e o
calendário principal costuma vir **sem o campo**. Filtrar só por `selected == true` excluiria
justamente o que não pode faltar — trocaria um bug por outro pior. Daí a ordem: primário primeiro,
por identidade, e só depois o filtro.

Sem a lista (rede fora, permissão negada) degrada para `"primary"` sozinho, em vez de ficar sem
agenda. E um calendário secundário que falha não derruba os outros — perder o import do Teams é
ruim, perder o principal junto seria pior.

### A duplicata que teria virado ruído diário

Uma reunião pode chegar pelos **dois** caminhos. Dois eventos idênticos e sobrepostos fazem
`ActiveEvent.Candidates` devolver dois, e a barra passa a perguntar *"2 reuniões agora — qual?"*
(§8) **sobre a mesma call**. O seletor do §8 existe para ambiguidade de verdade; disparar por
duplicata o transformaria em ruído e ensinaria a ignorá-lo.

`AgendaMerge.Dedupe` compara **título normalizado + início + fim**. O id não serve — é justamente o
que difere entre as cópias. Vence o primeiro, e o principal entra primeiro na lista porque é dele
que vêm o RSVP e o `conferenceData`.

Duas reuniões **diferentes** no mesmo horário continuam duas: isso é conflito de agenda de verdade,
e escondê-lo seria o oposto do que o produto faz.

### A origem no painel — sem gastar cor

O usuário pediu para distinguir Teams de Google, com a ressalva *"se isso for conflitar muito com
cores, melhor não fazer"*. **Não conflita**, e o D-017 já tinha aberto essa porta: a cor de
provedor do S3 é identidade e está declarada fora dos dois vocabulários da barra.

Aqui nem cor foi preciso: a origem é **texto apagado** na linha do S3, mais o "Veio de: …" na dica.
O principal não se anuncia — seria ruído em quase toda linha.

`Google.CalendarLabels` permite apelidar por id ou nome, porque "Calendário" não diz nada. É a
integração do Google impondo um nome ruim; o app contorna em vez de exibir o problema dela.

### O que já funcionava de graça

A cascata do D-017 leu os links do Teams do corpo dos convites importados sem nenhuma mudança —
`teams.microsoft.com/meet/...` saiu no despejo já classificado. A decisão de aceitar `location` e
`description` como fontes, tomada para o Zoom, pagou de novo aqui.

### `--dump-agenda`

Irmã da `--dump-tasks` (D-031). "Não aparece" pode ser calendário não lido, evento filtrado pelo
§2.1, ou janela de tempo, e a barra não distingue os três. Respondeu de primeira: três calendários,
"Backlog Pearson" presente, link do Teams extraído.

---

## D-035 — A pergunta certa sobre ordem Z é "de quem é o pixel?"

**Status:** Aceita · 2026-08-20 · encontrada em uso

### O sintoma

*"Se eu clico no botão iniciar do Windows, o Tempus some e fica assim até eu clicar em outro
lugar."*

### O diagnóstico, e quatro medidas que mentiram

Com o menu Iniciar aberto, **todos** os indicadores diziam que estava tudo bem:

| Medida | Resposta | Conclusão errada |
|---|---|---|
| `SHQueryUserNotificationState` | `QUNS_APP` | não é supressão de tela cheia |
| retângulo do `Shell_TrayWnd` | inalterado | a taskbar não recolheu |
| `IsWindowVisible` + `WS_EX_TOPMOST` + rect | tudo certo | a barra não se escondeu |
| `DWMWA_CLOAKED` | zero | o DWM não a ocultou |
| `EnumWindows` sobre o retângulo | a barra no topo | nada a cobre |

Só uma discordava: **`WindowFromPoint` no centro da barra devolvia `Shell_TrayWnd`**. E é ela que
manda — é quem decide de quem é o pixel e de quem é o clique.

A lição de método: a enumeração de ordem Z **não é confiável** para essa pergunta, e eu quase
descartei o único dado correto porque ele estava sozinho contra quatro.

### Dois defeitos, não um

**1. O guard perguntava por um proxy.** O D-021 checava `IsInFrontOf(barra, Shell_TrayWnd)` — que
respondia certo para o caso dele e errado aqui. Agora a pergunta é direta: *a barra é quem recebe o
pixel no próprio centro?* Se sim, não mexe. Se quem está lá é uma janela **nossa**, também não mexe
— é o menu de contexto ou a dica, e enterrá-los era exatamente o defeito que o D-021 consertou.

**2. Reafirmar `HWND_TOPMOST` numa janela que já é topmost é ignorado.** Este era o defeito
silencioso: mesmo depois de detectar corretamente que estava enterrada, a chamada de `SetWindowPos`
rodava a cada segundo **sem efeito nenhum**.

Detectar sem conseguir corrigir é pior que não detectar: gasta CPU e dá a impressão de que há uma
rede de segurança onde não há.

### A escada de três degraus, e por que não bastou um

A primeira correção — sair para `HWND_NOTOPMOST` e voltar — **eu dei por boa cedo demais**. O teste
que a validou fechava o menu clicando no próprio botão Iniciar; o caminho do usuário era clicar em
**outra janela**, e por ali ela continuava enterrada. Ele voltou dizendo isso, e estava certo.

O que funciona é uma escada, do mais barato ao mais invasivo, parando no primeiro que resolver:

1. **`NOTOPMOST` → `TOPMOST`.** Força reordenação dentro da faixa.
2. **Colocação relativa ao `Shell_TrayWnd`.** Pedir "logo acima daquela janela" em vez do topo
   genérico da faixa — explícito onde o genérico devolve uma posição abaixo dela.
3. **Esconder e re-exibir.** Uma janela recém-mostrada entra no **topo** da faixa, não na posição
   que tinha. Só roda com a barra já enterrada, ou seja, invisível: o piscar que causaria não tem
   como ser visto.

Cada degrau só acontece se o anterior falhou, verificado pela mesma pergunta — *de quem é o pixel?*

### O que ficou, honestamente

**Com o menu Iniciar aberto, a barra continua coberta**, e nenhum dos três degraus muda isso.
Curiosamente o `WindowFromPoint` já devolve a barra nesse estado, mas a tela não a mostra: o que
cobre ali é uma superfície composta pelo DWM, não uma janela que dispute *hit-test*. Insistir a
16 ms seria queda de braço com o shell — CPU à toa contra a regra 9, por um ganho que dura o tempo
de um menu aberto.

O que mudou, verificado com captura de tela no caminho exato do usuário: **clicou em qualquer
lugar, a barra volta.** Antes ficava enterrada indefinidamente — e sobrevivia até a reinicialização
do app, porque o estado é da taskbar, não nosso.

É a fragilidade estrutural que o D-002 aceitou ao escolher barra flutuante em vez de `SetParent`.
A escolha continua certa — `SetParent` em `Shell_TrayWnd` quebraria a cada atualização do Windows —
e este é o preço dela, agora com o limite conhecido e medido em vez de suposto.

---

## D-036 — O checkpoint do meio-dia pergunta sobre HOJE

**Status:** Aceita · 2026-08-20 · encontrada em uso · fecha a chave que o D-007 deixou pronta

### O alarme falso

*"Não gostei do tamanho e da importância que ganhou o texto 'Metade do dia: 2 tarefas abertas'.
Tenho uma call em 15 minutos e as tarefas são para amanhã ainda... mas as tarefas estão gritando
enquanto a reunião tá ali apagada, escrita em branco, sem importância."*

O despejo confirmou: as duas tarefas venciam **21/08**, e o dia era **20/08**. O
`MiddayCheckpoint` as contava assim mesmo, porque o `CountMode` nascia em `AllOpen` — toda tarefa
não concluída, com ou sem vencimento.

Resultado: âmbar preenchido gritando por trabalho de amanhã, ao lado de uma reunião a 15 minutos em
texto apagado. **A hierarquia visual estava invertida em relação à urgência real**, e é exatamente
o que a regra 1 existe para impedir: o âmbar apareceu sem motivo legível, e com isso empurrou para
segundo plano a única coisa irreversível no tempo que estava na tela.

### A chave já estava pronta

O D-007 escolheu `AllOpen` sabendo que podia se arrepender, e escreveu: *"se o vermelho das 17:00
incomodar depois de uma semana de convívio, mudar de AllOpen para DueTodayOrOverdue é uma linha de
configuração, e não uma discussão sobre o modelo"*.

Foi o que aconteceu — só que o incômodo veio primeiro pelo meio-dia, não pelas 17:00. A previsão
estava certa quanto ao arrependimento e errada quanto a qual dos dois avisos o produziria.

A pergunta que o checkpoint faz é **"estou em dia hoje?"**. Tarefa de amanhã não é resposta para
ela.

### O que se perde, e foi escolhido de olhos abertos

Com `DueTodayOrOverdue`, **tarefa sem vencimento não conta em nenhuma das duas fronteiras**. Quem
cria tarefa sem prazo deixa de ser lembrado dela ao meio-dia e às 17:00.

A alternativa avaliada era um terceiro modo — vencidas + de hoje + sem data — e foi recusada pelo
usuário: uma tarefa antiga sem data que ele nunca vai fazer alimentaria o aviso todo santo dia até
alguém datar ou concluir, o que reintroduziria o alarme falso por outra porta.

Há teste nomeado para essa contrapartida, para ela não voltar como bug.

### O que NÃO foi feito, de propósito

A outra metade da queixa — "a reunião fica apagada, sem importância" — tinha duas correções
possíveis: rebaixar as fronteiras do dia quando há call chegando, ou preencher o slot já em
`Approaching`. **Nenhuma foi feita.**

Corrigido o contador, neste cenário o chip simplesmente não aparece, e a reunião fica sozinha na
barra. Mudar duas coisas de uma vez deixaria sem saber qual funcionou — e a segunda gasta
preenchimento numa faixa bem maior do dia, contra o D-012. Se depois de conviver a call ainda
parecer apagada, o problema volta já isolado.

### Dois testes caíram, e foi a suíte funcionando

`Meio_dia_com_tarefas_e_ambar_nunca_vermelho` e `Fim_de_jornada_com_tarefas_e_critico_e_nasce_as_17h`
usavam tarefas **sem data**. Sob o padrão novo elas não contam, e os testes falharam na hora — não
por estarem errados, mas por codificarem o padrão antigo. Ganharam vencimento de hoje, que é o que
sempre quiseram dizer.

---

## D-037 — A próxima call é a coisa mais importante do dia, e a barra não dizia isso

**Status:** Aceita · 2026-08-20 · encontrada em uso

### A queixa, que tinha três causas

*"O 'em breve' fica meio apagado, um tom escuro quase apagado de bordô, e o texto branco avisando
quantos minutos também não me inspira urgência. A próxima call é a coisa mais importante do dia pra
mim. Notei que nem aparece o título dela direito, fica suprimido e com espaço sobrando na barra."*

Três defeitos independentes, todos meus, e dois deles introduzidos por decisões recentes.

### 1. Cor de preenchimento usada como texto

`Approaching` e `OffHours` pintavam o texto com `AttentionBackground` (`#B4530A`) — a cor de
**preenchimento**. Sobre a pílula escura ela vira o bordô apagado que o usuário descreveu.

**É o mesmo erro que o D-032 já tinha corrigido no contorno do chip**, repetido noutro lugar. Lá a
conclusão foi "contorno é um terceiro contexto e pede cor própria"; aqui a lição completa é mais
simples: **cor de fundo não serve de tinta**. As tintas (`*Ink`) existiam e não estavam sendo usadas
onde deviam.

Agora há teste medindo o contraste WCAG do texto de **cada humor** sobre o fundo em que ele de fato
assenta — incluindo a mistura do chip translúcido sobre a barra. Ele pegou de imediato outros três
casos que ninguém tinha visto: `OffHours` em 4,42:1, `EndingSoon` em 4,31:1, e o verde de `Free` do
**tema claro** em 3,41:1. O tema claro ninguém exercita; quem tem de pegá-lo é o teste.

`Unknown` é a única isenção, e é nomeada: o cinza apagado do `Offline` é decisão do §0, não defeito.

### 2. `Hidden` reservava o espaço do alarme que não estava lá

O D-033 escondeu o chip repetido com `Visibility.Hidden` em vez de `Collapsed`, justificando que
assim "o texto do compromisso ao lado não salta de largura a cada alarme que vai e vem".

**Foi o negócio errado.** `Hidden` reserva a largura do último motivo exibido — e o último era
"Metade do dia: 2 tarefas abertas". O resultado foi um vão morto no meio da barra enquanto o título
da reunião truncava por falta de espaço. Estabilidade de layout não vale o espaço da informação que
importa.

### 3. A pausa partia a região do compromisso

O slot de descanso vivia **entre** o compromisso e o chip, cortando em dois a única região que
precisa de largura contínua. Foi para o bloco fixo de indicadores à direita, ao lado das tarefas e
do e-mail — sugestão do usuário, e claramente certa: à direita ficam os números que se consulta, no
meio fica a frase que se lê.

### A urgência mora em um lugar só

Regra nova para o slot de tempo: **a cor da urgência fica na pílula quando ela é preenchida, e no
texto do compromisso quando ela é apenas tingida.** Em `Approaching` a pílula não preenche, então é
a contagem regressiva que acende — em branco ela não comunicava nada.

Não é uma terceira área colorida (I1): é a mesma área de tempo, que o §0.5 já trata como um
vocabulário só, se estendendo pelo detalhe que a acompanha.

### Um quarto defeito, achado na captura de tela

A verificação visual mostrou "Começando" ao lado de um chip "Backlog Pearson em 3 min" — o mesmo
fato duas vezes, que o D-033 deveria ter evitado.

Causa: **os limiares do humor e do sinal não se alinham.** O humor vira `Imminent` a 5 min; o sinal
só vira `MeetingImminent` a 2. Entre 5 e 2 minutos sobrava `MeetingUpcoming` sem par na tabela. Os
dois humores de "próxima reunião" passam a absorver os dois sinais de aproximação.

Os testes do D-033 não pegaram porque testavam a tabela contra ela mesma, e não contra os limiares
reais de `SignalThresholds` e `TimeThresholds`. Foi uma captura de tela que pegou.

---

## D-038 — Cada área da barra faz uma coisa só

**Status:** Aceita · 2026-08-20 · encontrada em uso

### O sintoma

*"Enquanto estou na call, não consigo abrir a lista de calls subsequentes, aquele grid que aparecem
todas as calls do dia."*

### A causa

`OnStatusClicked` caía em `OnTimeClicked` quando não havia nada a reconhecer. E `OnTimeClicked`
entra na call sempre que há `CallUrl`. Durante uma reunião **os dois cliques esquerdos da barra
faziam a mesma coisa**: entrar na call que já estava aberta.

A agenda do dia ficava sem porta esquerda, sobrando só o menu do botão direito — e o usuário não o
encontrou, o que é a definição de afordância que não existe.

### A correção

A pílula de estado abre a agenda; o texto do compromisso entra na call. Uma área, um significado —
que é o que o D-023 pretendia ao dar slots próprios ao estado e ao compromisso, e que o
encadeamento de `OnStatusClicked` para `OnTimeClicked` desfazia na prática.

O **▶** do D-037 é o que distingue os dois à vista: quem tem o glifo entra na call, quem não tem
mostra o dia. As duas mudanças se encaixam sem terem sido planejadas juntas — a afordância que eu
tinha acabado de acrescentar já era a legenda de que esta correção precisava.

Reconhecer continua ganhando de tudo na pílula: a regra 2 não abre exceção.

A dica também mentia — dizia "Clique para entrar na call" na pílula que não entra em call nenhuma.

### Verificação, e um erro no caminho

Testei primeiro na barra real e **silenciei um alarme do usuário sem querer**: o estado era
`Estourou`, o reconhecimento tem prioridade, e o clique o consumiu em vez de abrir a agenda.

A regra do D-024 — verificar escrita no modo demo, nunca na conta real — vale também para **gesto**:
um clique de teste na barra real consome estado real. A verificação foi refeita no demo, onde o
clique na pílula em `Ocupado` abriu a agenda do dia, que é exatamente o caso relatado.

---

## D-039 — O toast vira gesto, e a reunião avisa duas vezes

**Status:** Aceita · 2026-08-20 · encontrada em uso

### O sintoma

*"o toast deveria ser mais bem usado, até agora apareceu só uma vez, e quando fui clicar fechou.
na reunião atual, não apareceu o toast pra mim, deveria aparecer o toast e ficar aberto"*

Três queixas numa frase, e as três eram consequências diretas do escopo mínimo do D-028.

### Por que apareceu só uma vez

Nada tinha quebrado. Só existiam duas fontes de toast — `MeetingRanIntoNext` e `DayEnded` — e a
regra 1 quer justamente que elas sejam raras. Somados os ~5 segundos de tela e a retenção durante
apresentação, o aviso podia ter saído e passado despercebido.

O modelo estava certo; o **orçamento de interrupção** é que tinha ficado pequeno demais. O D-028
disse que subir esse escopo depois seria barato, e foi.

### A reunião, em dois tempos

O usuário desenhou o comportamento: *"um aviso com 10 minutos de antecedência e depois um fixo com
2 minutos de antecedência pra ficar ali até eu clicar nele pra entrar."*

| Momento | Tipo | Fica na tela? |
|---------|------|---------------|
| T−10 min | `MeetingHeadsUp` | não — e expira na Central quando a reunião começa |
| T−2 min | `MeetingStanding` | **sim, até o clique** |

Os dois limiares não foram inventados: `SignalThresholds` já usava exatamente 10 e 2.

Mesma etiqueta nos dois, como a entrada e a escalada do nível 3 já faziam — o fixo **substitui** o
passageiro em vez de empilhar dois avisos sobre a mesma reunião. E o mesmo engolimento: abrir o app
a um minuto da reunião emite só o fixo, porque duas interrupções em sequência não são dois avisos,
são um susto.

### Por que a reunião não pode ler o vencedor da arbitragem

`MeetingUpcoming` é nível 1. Qualquer `TaskOverdue` âmbar o derrota na arbitragem — o aviso da call
sumiria por causa de uma tarefa vencida, que não tem nada com ela.

Então o caminho de reunião lê o **humor temporal**, e não o sinal vencedor. O slot de tempo mostra
a próxima reunião independentemente do que mais esteja alarmando, então a propriedade que o D-028
protegia continua de pé — *o toast nunca fala de algo que a barra não está mostrando* — só que por
outra superfície da barra.

O link, porém, vem do **evento**, e não de `TimeStatus.CallUrl`, que é `null` em `Approaching` de
propósito (D-016): lá o silêncio protege contra clicar em "Em breve" e cair numa sala vazia. No
toast não há essa ambiguidade — ele nomeia a reunião e o botão nomeia a ação.

### `scenario="reminder"` exige botão, e falha calado sem ele

É o que faz o aviso ficar pré-expandido e esperar por um gesto. A regra que morde:

> You must provide at least one button on your app notification... otherwise the notification will
> be treated as a normal notification.

Sem botão o Windows **não reclama** — ele degrada para os 5 segundos de sempre. Pareceria que a
decisão foi tomada e ela teria sido ignorada, que é o modo de falhar mais caro que existe aqui.
Por isso "fixo" e "tem botão" andam juntos, e a amarração é teste, não comentário.

É também por isso que uma reunião presencial, sem link de call, ainda sai com "Dispensar": o botão
não está lá por conveniência, está lá para sustentar o cenário.

### O nível 3 também passou a ficar na tela

Não era o pedido, mas estava errado desde o D-028: a regra 2 diz que o nível 3 **escala e não
decai**, e um vermelho que interrompe por cinco segundos e some decai. O toast era raro demais para
alguém notar a contradição.

### Ativação sem servidor COM

Três tipos de botão, e a diferença entre eles é **de quem dependem**:

| Botão | Ativação | Depende de |
|-------|----------|------------|
| `Entrar na call` | `protocol` com a URL crua | ninguém — o shell dá `ShellExecute` |
| `Dispensar` | `system` / `dismiss` | ninguém — tratado pelo shell |
| `Eu vi` | `foreground` → evento `Activated` | o processo vivo segurar o objeto |

A escolha de `protocol` para o botão que mais importa é deliberada: **entrar na call funciona mesmo
que tudo que escrevemos esteja quebrado.**

O `Eu vi` e o clique no corpo dependem de `ToastNotification.Activated` disparar no processo. Para
app não empacotado esse é o caminho documentado no quickstart do Win32, e não exige CLSID nem
servidor COM — mas é notório por falhar em silêncio, e havia plano B escolhido (esquema `tempus:`
em `HKCU` mais named pipe).

**A sonda decidiu antes de eu escrever o resto.** `--toast-probe` passou a emitir um aviso fixo com
botões e a esperar até 60 s, relatando o que voltou — em `MessageBox` e em arquivo. Devolveu
`ativacao=OK` com o argumento `ack|probe|sonda`, e o plano B foi descartado sem custo. É a mesma
jogada do `--dump-tasks` e do `--dump-agenda`: transformar "não funcionou" em resposta.

A contrapartida entrou no código como comentário porque é invisível: soltar a referência ao
`ToastNotification` é perder o clique sem erro nenhum. Daí o dicionário de toasts vivos.

### Retirada automática

`Withdraw` só chamava `History.Remove`, que tira da Central e **não** tira da tela. Com toast fixo
isso deixaria um aviso já resolvido pendurado — dado velho com cara de atual, que é o que a regra
10 proíbe. Agora faz as duas metades, e o aviso sai por três caminhos: o botão, o clique na barra,
e a situação ter acabado sozinha.

### Fora de escopo, por escolha do usuário

Meio-dia e Offline continuam sendo cor e não interrupção. O `MiddayCheckpoint` estava na tabela do
§5 desde o começo, mas o usuário tinha reclamado no dia anterior (D-036) de ele gritar sem motivo —
promovê-lo a interrupção diária, no horário mais previsível do dia, andaria para trás.

Furar o Não Perturbe no nível 3, que o §5 pede, continua pendente: precisa de `scenario="urgent"`,
que no Windows 11 depende de marcar o Tempus como prioritário nas Configurações. É passo manual,
não código.

---

## D-040 — `showAssigned` nasce falso, e tarefa atribuída era invisível

**Status:** Aceita · 2026-08-21 · encontrada em uso

### O sintoma

*"estou fazendo uma automação de tarefas pós daily no Espaços do Google Chat, e fui testar lá,
criei uma tarefa chamada Teste e atribuí pra mim e não apareceu no Tempus"*

### A medição, antes do palpite

`--dump-tasks` respondeu em uma execução: a API devolvia **uma lista só** (`Minhas tarefas`) e a
tarefa `Teste` não estava nela. Isso descartou de saída a hipótese mais fácil — "existe uma lista
de tarefas do Espaço que não estamos lendo" —, porque nenhuma lista nova aparecia em
`tasklists.list`.

### A causa

O `tasks.list` tem um parâmetro que a documentação descreve assim:

> **showAssigned** — Flag indicating whether tasks assigned to the current user are returned in the
> result. **Default: False.**

Ele governa exatamente as tarefas atribuídas a você a partir de **outra superfície**: Espaços do
Chat, Documentos e Gmail. Não é limite da API nem escopo OAuth faltando — é um parâmetro opcional
cujo padrão exclui essas tarefas, e nós nunca o tínhamos mandado.

O padrão é infeliz na mesma direção do D-031: silencioso, plausível, e do lado errado. Nada falha,
nada avisa; a tarefa simplesmente não vem.

### A correção

`ShowAssigned = true` nas três consultas — a de abertas, a de concluídas e a da sonda. A
verificação foi imediata: `Teste` passou a vir, com
`origem: SPACE · https://mail.google.com/chat/#chat/space/AAQAqqZBFag`.

A sonda passou a imprimir a `origem` de cada tarefa a partir do `assignmentInfo`
(`surfaceType` + `linkToTask`), porque "de onde veio esta tarefa" agora é uma pergunta legítima e
antes não era.

A biblioteca instalada (`Google.Apis.Tasks.v1` 1.74.0.3958) já expõe `showAssigned` e
`assignmentInfo` — conferido no binário antes de mexer, para não trocar de pacote à toa.

### Isto não contradiz o D-004

O D-004 tirou o **Chat** do escopo por não haver contagem de não-lidos sem um N+1 caro. Continua
valendo: o Tempus não lê mensagem nem conta não-lida.

Uma tarefa atribuída num Espaço é outra coisa — ela é uma tarefa do **Tasks**, chega pela API do
Tasks, com o escopo que já temos, numa consulta que já fazíamos. O Espaço é só onde ela nasceu.

A distinção está anotada no `SPEC.md` porque é fácil de errar nos dois sentidos: uma sessão futura
poderia "consertar" removendo isto em nome do D-004, ou "completar" a integração lendo o Chat.

### O que muda no contador

Tarefa atribuída passa a contar como tarefa aberta, que é o comportamento certo — ela é sua. Sem
vencimento, ela **não** aciona o vermelho das 17:00 nem o checkpoint do meio-dia, porque o D-036 já
trocou o padrão para `DueTodayOrOverdue`. As duas decisões se encaixam sem terem sido pensadas
juntas: se o `CountMode` ainda fosse `AllOpen`, esta correção teria ligado um alarme falso por
tarefa que a automação criasse.

Nenhuma chave de configuração por ora. Não é preferência, é tarefa que estava sumindo — e se um dia
o volume da automação incomodar, aí sim vale um interruptor.

### A escrita funciona — verificado, não inferido

Concluir uma tarefa atribuída **funciona nos dois sentidos**: marcada no Tempus, some do Espaço;
marcada no Espaço, some do Tempus. Verificado pelo usuário na tarefa `Teste`, no mesmo dia.

A leitura da documentação tinha apontado para cá — `parent` e `deleted` aparecem como
somente-leitura para tarefas atribuídas e `status` não —, mas isso era inferência de ausência, que
é o tipo de raciocínio que já errou neste projeto (D-031). Agora é observação.

Consequência prática: uma tarefa nascida na automação do Espaço é, para o Tempus, uma tarefa como
qualquer outra — conta no contador, aceita o clique de concluir, passa pela `WriteQueue` do D-029 e
volta atrás sozinha se a escrita falhar. Nenhum caso especial no código.

Isto **não** fecha o critério de aceite 7, que fala de **criar** tarefa nos dois sentidos. O que
está verificado é conclusão.

---

## D-041 — "Já saí desta reunião": o gesto que faltava para a call que acaba cedo

**Status:** Aceita · 2026-08-21 · encontrada em uso

### O sintoma

*"as calls que terminam antes do tempo e eu não tenho como cancelar o aviso e contagem de tempo
antes de terminar a contagem no Tempus... terminei a call antecipadamente, preciso ter uma forma
de cancelar essa contagem"*

### Não é desconforto, é alarme falso

A call acabou 14:38; a agenda diz 15:00. Encadeado, isso produzia:

1. `Ocupado · faltam 22 min` — cronograma exibido como se fosse realidade;
2. âmbar em `Encerrando` às 14:55;
3. e, se a seguinte começasse às 15:00, **`MeetingRanIntoNext`** — nível 3, que escala, pisca e
   cobra clique — por uma reunião deixada vinte minutos antes.

A regra 1 diz que vermelho à toa é modelo errado, não tolerância do usuário. E a contagem
regressiva de uma reunião encerrada é a regra 10 sendo violada pelo humor temporal.

Nenhum gesto resolvia. Em `InMeeting` o humor não tem ocorrência, então `CanAcknowledge` é falso e
o clique não faz nada. Reconhecer `Encerrando` só devolvia `Ocupado`, com a contagem intacta.

### A decisão

Um gesto no menu do botão direito — **"Encerrei esta reunião"** —, e a partir dele a reunião sai
do humor, do evento ativo e do ciclo de call.

É a forma do **D-006**, que este projeto já escolheu uma vez: o Tempus não infere presença, o
usuário declara. Detectar sozinho que a call acabou exigiria microfone ou janela, e continua fora.

**Remove, não trunca.** Encurtar o fim para "agora" faria nascer um `MeetingEnded` — âmbar por três
minutos. Responder a um "já terminei" explícito com um alerta é a resposta errada; a pergunta que o
produto faz é "algo precisa de mim agora?", e a resposta verdadeira depois do gesto é silêncio.

**Alterna**, como o gesto da pausa: um clique só, que grava em disco e vale o resto do dia, precisa
de caminho de volta. O item vira "Reabrir «Daily KA»" enquanto a reunião ainda estaria em curso, e
some do menu depois disso — passado o horário, reabrir não teria efeito nenhum.

### Onde a ocorrência mora

No `AcknowledgementStore` que já existe. Ele já é um conjunto de ocorrências por dia, persistido,
carregado na subida e limpo na virada; e já guarda strings heterogêneas (`Overrun|…`,
`DayEnded|2026-08-21`). `MeetingLeft|…` é mais uma ocorrência com nome próprio, não um conceito
novo enfiado num lugar alheio. O §7 ganhou uma seção separando as duas espécies de supressão,
porque "eu vi" suprime o **alarme** e "já saí" suprime o **fato**.

Nada disto sobe para o Google: a reunião continua no calendário para todo mundo. O que muda é só o
que o Tempus cobra. Por isso o gesto não passa pela `WriteQueue` e a regra 12 não se aplica — não
há escrita que possa falhar em silêncio.

### Filtrar uma vez, na entrada do domínio

`BuildState` calcula a agenda filtrada e passa a mesma lista ao humor, ao `ActiveEvent.Candidates`
e aos sinais. Filtrar em três lugares convidaria os três a discordarem.

Duas coisas caíram de graça: a escolha do §8 se limpa sozinha, porque o evento deixa de ser
candidato; e o toast fixo da reunião é retirado da tela pelo `Retire` do D-039, que já tira o que o
humor não nomeia mais.

O **painel do dia continua com a agenda inteira**. A reunião aconteceu; o gesto para de cobrar
atenção, não reescreve o dia.

### O bug que os testes pegaram antes do usuário

A primeira versão de `Leavable` devolvia "a primeira reunião em curso". Com duas reuniões coladas
às 15:05, a barra mostra **duas coisas sobre reuniões diferentes** (§0.5): o humor diz `Ocupado`
pela Review, e o chip acende vermelho pela Daily que estourou. Aquela versão teria encerrado a
reunião errada em alguns estados.

A correção foi amarrar o gesto ao que o humor nomeia, e exigir que ela esteja de fato correndo.
Efeito colateral bom: em `Estourou` o item some do menu, e sobra o "reconhecer alerta", que é o
gesto certo para aquele estado — lá o problema não é a contagem, é o alarme.

Escrever o teste também corrigiu uma crença minha: `Estourou` só existe quando **nada** está em
curso, porque reunião correndo sempre ganha no resolvedor. Eu tinha suposto o contrário.

### Verificação

Domínio coberto por 15 testes, sendo o principal o que justifica a mudança: Daily encerrada às
14:38, Review começando às 15:00, e **`MeetingRanIntoNext` não dispara**.

O gesto em si foi exercitado no **demo**, que ganhou a mesma filtragem e responde ao clique de
verdade — D-024 e D-038 dizem que gesto se verifica ali, e não na barra real.
