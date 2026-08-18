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
