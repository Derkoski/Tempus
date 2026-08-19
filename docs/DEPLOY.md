# Instalar em outra máquina

Não há instalador, e isso é proposital (`SPEC.md` → fora de escopo). São dois arquivos copiados e
um consent. Cinco minutos.

## Gerar o pacote

```bash
dotnet publish Tempus/Tempus.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o publish
```

Saem dois arquivos em `publish/`:

| Arquivo | O que é |
|---|---|
| `Tempus.exe` | ~72 MB, **autocontido** — traz o .NET dentro |
| `appsettings.json` | sua configuração, editável ao lado do exe |

**Autocontido é a decisão que importa aqui.** Num notebook corporativo você provavelmente não
consegue instalar o .NET Desktop Runtime sem passar pela TI. Assim não precisa: o exe não depende
de nada instalado, e não exige direitos de administrador para rodar.

`publish/` está no `.gitignore` — binário de 72 MB não entra no repositório.

## Instalar

```powershell
.\scripts\install.ps1 -Publish
```

O script gera o pacote, instala e configura tudo. Sem `-Publish`, usa o conteúdo de `publish/` como
está. Com `-NoStartup`, não inicia junto com o Windows.

O que ele faz:

| Onde | O quê |
|---|---|
| `%LOCALAPPDATA%\Programs\Tempus\` | executável e configuração |
| Menu Iniciar | atalho `Tempus` |
| Pasta Inicializar | atalho, para subir com o Windows |

**Nada exige administrador** — tudo acontece dentro do seu perfil.

Três detalhes que o script resolve e que dão dor de cabeça manualmente: encerra a instância em
execução antes de trocar o exe (substituir um binário em uso falha com erro obscuro), **preserva um
`appsettings.json` já existente** numa reinstalação, e roda `Unblock-File` para o SmartScreen não
avisar de novo.

**Nunca rode a partir de `bin/` ou `publish/`.** `bin/` é apagado a cada rebuild e `publish/` é
sobrescrito — foi assim que uma instância "sumiu" depois de um reinício. O destino em
`%LOCALAPPDATA%\Programs` existe para ser estável.

Para remover: `.\scripts\uninstall.ps1` (acrescente `-PurgeData` para apagar também as credenciais).

## Na máquina nova, o que mais é preciso

**Copie o `client_secret.json` para `%APPDATA%\Tempus\`.** É o mesmo arquivo nas duas máquinas — um
OAuth client identifica o *app*, não a máquina.

**Não copie a pasta `tokens`.** O token é cifrado com DPAPI vinculado ao usuário *e* à máquina; do
outro lado ele não descriptografa, e o app trata como ausente. A barra sobe cinza, você clica,
autoriza, e pronto. É o caminho correto.

Se você não tiver o SDK do .NET na máquina nova, gere o pacote **aqui**, copie a pasta `publish/`
para lá e rode o `install.ps1` sem `-Publish`.

**Confira as notificações**, porque elas falham em silêncio:

```powershell
& "$env:LOCALAPPDATA\Programs\Tempus\Tempus.exe" --toast-probe
```

Deve aparecer um toast de exemplo e uma caixa dizendo o que aconteceu. O app precisa de um atalho
no Menu Iniciar carregando o *AppUserModelID* `Sponte.Tempus` — ele grava isso sozinho no atalho
que o `install.ps1` cria, na primeira subida. Sem o atalho, o Windows **aceita** a chamada e não
mostra nada, que é por que a sonda existe (D-028). Se ela disser que o canal abriu e mesmo assim
nada aparecer, o problema está em *Configurações › Sistema › Notificações*.

## O que esperar de atrito

**SmartScreen vai avisar** que o app não é reconhecido, porque o executável não é assinado. É
*Mais informações → Executar assim mesmo*, **uma vez por máquina**. Ser administrador não muda
isso: o aviso é de reputação do binário, não de permissão. Assinar exigiria certificado de code
signing pago, desproporcional para ferramenta de uso pessoal.

Se o Windows marcar o arquivo como vindo da internet (ao baixar de OneDrive, e-mail ou Teams), o
aviso reaparece. Para remover a marca:

```powershell
Unblock-File C:\Tools\Tempus\Tempus.exe
```

**Com conta de administrador, AppLocker e política de execução deixam de ser problema** — é o caso
aqui. Antivírus corporativo ainda pode reclamar de um exe grande e não assinado; se acontecer, uma
exclusão de pasta resolve.

## Como as duas máquinas convivem

**Não há sincronização entre máquinas, e não precisa haver.** Tarefas e agenda vivem no Google;
cada instância lê e escreve lá. Concluir uma tarefa no notebook aparece em casa no próximo ciclo,
em até 15 segundos. É por isso que "sincronização entre máquinas" está no fora-de-escopo do
`SPEC.md` — o Google já é o ponto de encontro.

Cada máquina tem seu próprio token, com seu próprio prazo de 7 dias (D-003). Então o re-consent
semanal acontece **duas vezes**, uma em cada lugar, em dias possivelmente diferentes. Chato, mas
consequência direta do modo Testing, que é permanente.

O `appsettings.json` é por máquina. Se você mudar horário de expediente ou consulta de e-mail em
uma, precisa repetir na outra — ou copiar o arquivo.
