# Tempus — Especificação de Produto

## Problema

O dia de trabalho é conduzido por informação que vive em quatro lugares diferentes (Google
Calendar, Tasks, Gmail, e o relógio) e nenhum deles está no seu campo de visão enquanto você
trabalha. O resultado: você entra atrasado em call, segura uma call além do horário sem
perceber que já invadiu a próxima, e chega às 17:00 sem saber o que ficou aberto.

## Proposta

Uma barra permanentemente visível sobre a taskbar do Windows, com largura de 3–5 slots de
ícone, que responde a uma pergunta só: **"algo precisa de mim agora?"** Na maior parte do dia
a resposta é não, e a barra fica neutra mostrando apenas números. Quando a resposta é sim, ela
muda de cor e diz o motivo em texto.

Princípio norteador: **a barra é um sinal, não um dashboard.** Todo detalhe vive em painéis
que abrem sob demanda. A barra em si carrega o mínimo.

## Usuário

Um usuário só (o desenvolvedor), em máquina Windows 11, conta Google Workspace corporativa
(TOTVS). Não é software para distribuir — é ferramenta pessoal. Isso permite decisões que não
seriam aceitáveis em produto: sem multi-conta, sem multi-idioma, sem instalador, sem
onboarding.

## Superfícies

### S1 — A barra (sempre visível)

Janela sem borda, always-on-top, posicionada sobre uma região vazia da taskbar. Largura
padrão ≈ 5 slots (~200px a 100% DPI, escalando com o DPI do monitor).

Layout, da esquerda pra direita:

```
calmo (maior parte do dia):
┌────────────────────────────────────────────────────────┐
│  15:00 Review de sprint  │              │  ☑ 6  │ ✉ 12 │
└────────────────────────────────────────────────────────┘
     o que vem a seguir       (vazio)      tarefas  email

com alerta:
┌────────────────────────────────────────────────────────┐
│  15:00 Review  │ Daily acabou — Review já começou │ ☑ 6 │ ✉ 12 │
└────────────────────────────────────────────────────────┘
    âncora, sem cor        motivo/estado (colorido)
```

- **O que vem a seguir** ocupa o slot esquerdo. Onde antes havia um relógio — redundante, porque
  o Windows já mostra a hora a três centímetros dali (D-011). Hora absoluta em destaque, título
  em texto secundário, truncado. Nunca colore. Clicar abre a agenda (S3).
- **Motivo/estado** é a área que muda de cor. É o único elemento que colore, e fica **vazio**
  quando não há nada exigindo ação. Os dois slots têm papéis distintos e não se repetem: o
  esquerdo é âncora ("o que vem depois", sempre presente), o do meio é alarme ("algo precisa de
  mim agora", raro).
- **Contadores** (tarefas, e-mail) permanecem neutros mesmo com a barra vermelha — a cor
  pertence ao estado, não aos números (`SEVERITY.md` I6).
- Quando não há motivo ativo, a região de motivo mostra o próximo compromisso do dia, ou
  fica vazia se não houver.
- Contadores de 3+ dígitos exibem `99+`.

Interações:

| Ação | Resultado |
|------|-----------|
| Clique no "o que vem a seguir" | Abre painel de agenda (S3). Nunca reconhece alerta. |
| Clique no contador de tarefas | Abre painel de tarefas (S2) |
| Clique no contador de e-mail | Abre o Gmail no navegador |
| **Clique na área de motivo, com alerta ativo** | **Reconhece o alerta** — "eu vi". Volta ao normal. |
| Clique na área de motivo, sem alerta | Abre painel de agenda (S3) |
| Clique em qualquer lugar, no estado `Offline` | Dispara o re-consent (`SEVERITY.md` §6) |
| Clique direito em qualquer lugar | Menu: sincronizar agora, abrir agenda, configurações, sair |
| Hover | Tooltip com o motivo completo e horário da última sincronização |

O clique é o gesto central do produto: substitui integralmente a detecção de presença em call
(D-006). Como o alerta persiste e escala até ser reconhecido, reconhecer precisa ser a ação mais
fácil possível — um clique na área que está colorida, sem menu e sem confirmação.

### S2 — Painel de tarefas

Abre acima da barra. Lista todas as tarefas abertas do Google Tasks, agrupadas por
vencimento (Vencidas / Hoje / Depois / Sem data). Permite:

- Marcar concluída (reflete no Google Tasks)
- Criar tarefa nova, com título e vencimento opcional (reflete no Google Tasks)
- Editar título e vencimento
- Fecha ao perder foco ou com `Esc`

### S3 — Painel de agenda

Abre acima da barra. Timeline do dia com os eventos, destacando o atual e o próximo, e
mostrando visualmente os intervalos (ou a falta deles) entre eventos consecutivos. Clique num
evento com link de Meet abre a call.

### S4 — Toasts

Notificações nativas do Windows para os eventos definidos em `SEVERITY.md` §4.

## Integrações

| Serviço | Uso | Direção | Escopo OAuth | Status |
|---------|-----|---------|--------------|--------|
| **Calendar** | Eventos do dia, horários, links de Meet | Leitura | `calendar.readonly` (sensitive) | Fase 1 |
| **Tasks** | Tarefas abertas, criar/concluir/editar | **Bidirecional** | `tasks` (sensitive) | Fase 1 (leitura), Fase 4 (escrita) |
| **Gmail** | Apenas a contagem de não-lidos | Leitura | `gmail.readonly` (restricted) | Fase 5 |
| Keep | — | — | — | **Fora de escopo** (D-004) |
| Chat | — | — | — | **Fora de escopo** (D-004) |

Detalhes de sincronização:

- **Sem webhooks.** Push do Google exige endpoint HTTPS público; não vale a pena para app
  local. Tudo é polling com delta.
- **Calendar:** polling com `syncToken` incremental. 60s de intervalo. Fallback para sync
  completo quando o token é invalidado (HTTP 410).
- **Tasks:** polling com `updatedMin` + `showDeleted=true` para capturar exclusões. 60s.
- **Gmail:** `users.labels.get` no label `UNREAD` → campo `messagesUnread`. Uma chamada,
  sem tocar em conteúdo de mensagem. 120s.
- **Fila offline:** escritas em Tasks feitas sem rede vão para uma fila local e são
  reproduzidas na volta da conexão. Conflito resolve por *última escrita vence*, com o lado
  do Google ganhando em empate (é a fonte da verdade).

## Fora de escopo (explícito)

Registrar aqui evita que uma sessão futura "ajude" adicionando isso:

- Múltiplas contas Google
- Instalador / distribuição / auto-update
- Sincronização entre máquinas
- Google Keep e Google Chat (D-004)
- Ler ou exibir conteúdo de e-mail — **só a contagem**
- Criar ou editar eventos de calendário (leitura apenas)
- **Tarefas com horário exato.** A API do Google Tasks descarta a hora e grava só a data, então
  uma "tarefa das 14:30" não existe do outro lado. O slot de "o que vem a seguir" é alimentado
  somente pela agenda (D-011). Não tentar contornar com hora no título nem com armazenamento local.
- Suporte a Windows 10 ou anterior
- Qualquer telemetria ou envio de dados para fora da máquina

## Critérios de aceite do v1

1. A barra fica visível sobre a taskbar e sobrevive a: restart do explorer, mudança de DPI,
   bloqueio/desbloqueio de sessão, e conexão/desconexão de monitor.
2. Ela se esconde quando um app entra em tela cheia, e volta ao sair.
3. Ela nunca aparece no Alt+Tab nem na própria taskbar.
4. Uma reunião real da minha agenda produz a sequência correta: `Info` (T−6), `Attention` (T−1),
   `Attention` ao começar, e `Critical` no fim se a próxima já começou. Uma reunião isolada que
   termina volta a `Calm` sozinha em 3 min, sem exigir clique.
5. Um alerta vermelho passa a piscar âmbar↔vermelho 5 min depois de aparecer, e **um clique na
   barra o remove imediatamente**.
6. Às 12:00 fica âmbar com a contagem correta de tarefas abertas; às 17:00 fica vermelho se
   sobrou alguma, e neutro se não sobrou. Em feriado nacional, nenhum dos dois dispara.
7. Criar uma tarefa no painel aparece no Google Tasks em < 60s, e vice-versa.
8. A contagem de e-mail não lido bate com o Gmail.
9. Quando o token expirar (vai acontecer toda semana, D-003), a barra fica **cinza dessaturada**
   com os contadores em `—`, e um clique reautoriza em dois cliques de navegador sem reiniciar o
   app. Nunca mostra dados velhos como se fossem atuais.
10. Uso de CPU em repouso indistinguível de zero; nenhuma alocação por frame na barra. A animação
    de piscar não é exceção — ela só existe enquanto há alerta nível 3 não reconhecido.
