# NexusGuard

**Tudo que o seu Windows precisa, num só lugar.**

Utilitário de manutenção para Windows 10/11 em **C# / WPF (.NET 10)**, sem dependências externas:
tudo assenta em APIs do próprio Windows (winget, Microsoft Defender, agente do Windows Update,
robocopy, powercfg, schtasks, DISM, SFC, WMI) e em chamadas nativas (`psapi`, `ntdll`, `shell32`,
`kernel32`).

Interface em **português do Brasil**. Público: técnicos de assistência e usuários avançados,
em uso local num único PC.

---

## Princípios

A aplicação foi construída à volta de quatro regras, visíveis na primeira execução:

| Faz | Nunca faz |
|---|---|
| Cria ponto de restauração antes de alterar | Apaga documentos, fotos ou senhas |
| Move arquivos para quarentena (7 dias) | Envia dados pessoais para a internet |
| Instala só drivers assinados pela Microsoft | Instala programas extras ou barras |
| Mostra pré-visualização antes de apagar | Altera o sistema sem confirmação |
| Registra tudo com opção de desfazer | Roda como administrador sem necessidade |

A elevação é pedida **por ação** (UAC on-demand), não no arranque.

---

## Módulos

### Início
**Visão geral** — análise completa em ~40 s com barra de progresso e passo textual. Seis cartões
clicáveis (aplicativos desatualizados, drivers pendentes, espaço recuperável, registro, ameaças,
inicialização) com ponto de severidade, recursos em tempo real e as cinco últimas ações.
Botão **Corrigir tudo com 1 clique**.

### Atualizar
- **Aplicativos** — winget, com parser de tabela independente do idioma do sistema.
- **Drivers** — agente do Windows Update (serviço Microsoft Update), atualizações do Windows e
  dispositivos com problemas.
- **Desinstalar** — programas do registro, classificados como *Bloatware*, *Inseguro*,
  *Redundante* ou *Em uso*; desinstalação silenciosa e varredura de resíduos para a quarentena.

### Limpar e otimizar
- **Limpeza** — 15 categorias com tamanho e nível de risco; **pré-visualização obrigatória** antes
  de remover, com filtros por categoria e aviso do que foi protegido; painel de **quarentena** ao
  lado, com restaurar/apagar por lote.
- **Registro** — extensões órfãs, App Paths inválidos, desinstaladores inexistentes, SharedDLLs
  ausentes, itens Run quebrados e históricos MRU. Exporta sempre um `.reg` antes de corrigir.
- **Performance** — três modos (Equilibrado / Trabalho / Jogos), métricas de CPU e memória,
  liberação de memória, processos mais pesados e seis ajustes de sistema com ganho estimado.
- **Inicialização** — mesma chave `StartupApproved` do Gerenciador de Tarefas; sempre reversível.

### Proteger
- **Hardware** — CPU, GPU, memória, discos (com S.M.A.R.T.) e bateria, com estado por componente
  e teste de estresse de 5 minutos.
- **Segurança** — estado completo do Defender e da firewall, análise rápida/completa/de pasta,
  remoção de ameaças, `sfc /scannow` e `DISM /RestoreHealth`.
- **Privacidade** — oito chaves documentadas (telemetria, ID de publicidade, Cortana, localização,
  sugestões, histórico de atividades, bloqueio de telemetria por `hosts`, câmera/microfone).
- **Backup** — robocopy em três modos (incremental, espelho, instantâneo), restauro, ponto de
  restauro e imagem completa do sistema (`wbadmin`).

### Sistema
- **Agendamento** — seis tarefas no Agendador de Tarefas do Windows, que correm **sem interface**
  (ver «Linha de comandos» abaixo), registam no histórico e notificam pelo ícone da bandeja.
- **Histórico** — tudo o que foi alterado, com **Desfazer** por ação e exportação de
  **relatório PDF**; segundo separador com o registro técnico.
- **Configurações** — geral, segurança (retenção, quarentena) e avançado, incluindo **modo simular**
  (dry-run), que calcula e regista tudo sem alterar nada.

---

## Linha de comandos e bandeja

Sem argumentos, o executável abre a janela normal. Com um argumento de trabalho, corre **sem
abrir janela nenhuma**, regista tudo no histórico, notifica pelo ícone da bandeja e sai com
código 0 (sucesso) ou 1 (falha) — que é o que o Agendador de Tarefas regista.

| Argumento | O que faz |
|---|---|
| `--tray` | arranca minimizado na bandeja |
| `--scan` | análise completa, sem alterar nada |
| `--clean` | limpeza das categorias seguras |
| `--update-apps` | atualiza os aplicativos via winget |
| `--backup` | backup incremental para o destino guardado |
| `--check-drivers` | procura drivers pendentes no Windows Update |
| `--quick-scan` | atualiza as definições e corre a análise rápida do Defender |
| `--help` | mostra a ajuda |

São exactamente estes os argumentos que as tarefas de **Agendamento** passam ao executável.

O `--backup` usa o destino e as pastas da última cópia feita pela interface — a página Backup
guarda-os em `settings.json`. Sem destino guardado, a tarefa avisa em vez de falhar em silêncio,
e a página Agendamento mostra «sem destino guardado» ao lado da tarefa.

**Bandeja** — o ícone usa `Shell_NotifyIcon` directamente, sem WinForms nem bibliotecas externas;
o ícone vem do próprio executável. Clique esquerdo abre a janela, clique direito dá
«Abrir / Analisar o PC agora / Sair». Com «Manter na bandeja ao fechar» ligado, fechar a janela
apenas a esconde — a aplicação continua disponível para as tarefas agendadas.

**Notificações** — são os balões do próprio ícone, que no Windows 10/11 aparecem como toast. Há um
botão «Testar notificação» em Configurações para confirmar que funcionam nesta máquina.

---

## Quarentena

Em vez de apagar, a limpeza move os arquivos para
`C:\ProgramData\NexusGuard\Quarantine\<aaaaMMdd-HHmm>\`, preservando o caminho de origem num
`manifest.json`. Restaurar devolve cada arquivo ao lugar original. A purga automática acontece aos
7 dias (configurável, 1–90).

Uma **lista branca** garante que documentos, imagens, vídeos, música, área de trabalho, downloads,
OneDrive, `AppData\Roaming`, pastas de sistema e perfis de navegador nunca são tocados — as caches
dentro desses perfis continuam a poder ser limpas.

Arquivos bloqueados por outro programa são agendados para remoção no próximo arranque
(`MoveFileEx` com `MOVEFILE_DELAY_UNTIL_REBOOT`).

---

## Desfazer

O histórico é um JSON Lines em `C:\ProgramData\NexusGuard\Logs\history.jsonl` (o registo técnico fica ao lado, em
`nexusguard-AAAAMMDD.log`). Cada entrada guarda
o necessário para reverter:

| Tipo | Como reverte |
|---|---|
| Lote de quarentena | repõe os arquivos nos caminhos originais |
| Correção do registro | importa o `.reg` exportado antes |
| Chave de privacidade | repõe o valor anterior |
| Item de inicialização | repõe o estado anterior |

---

## Compilar

Requisitos: **Windows 10 1809+** ou **Windows 11** (x64) e **.NET 10 SDK**.

```powershell
.\build.ps1 -SelfContained
```

Produz `dist\NexusGuard.exe` com o .NET incluído (~130 MB, não precisa de runtime instalado).
Sem `-SelfContained` o executável fica com ~1 MB mas exige o .NET 10 na máquina de destino.

Durante o desenvolvimento:

```powershell
dotnet run --project src\NexusGuard\NexusGuard.csproj
```

## Instalador

```powershell
.\build.ps1 -SelfContained -Installer
```

Gera `dist\NexusGuard-Setup-1.0.0.exe` (~44 MB) com **Inno Setup 6**. Se o compilador não
estiver instalado, o script avisa e indica `winget install JRSoftware.InnoSetup`; procura-o em
Program Files e também em `%LocalAppData%\Programs`, onde o winget o coloca quando corre sem
elevação.

O instalador:

- assistente em português do Brasil e inglês;
- instala em `C:\Program Files\NexusGuard\`;
- cria `C:\ProgramData\NexusGuard\{Logs,Quarantine,Reports,RegistryBackups}` com permissão de
  escrita para os usuários;
- atalhos no Menu Iniciar e, opcionalmente, na área de trabalho;
- entrada em «Aplicativos instalados» com ícone, editor e versão;
- desinstalador que pergunta se mantém a quarentena e os relatórios;
- **sem** ofertas de terceiros.

O arranque automático fica a cargo da própria aplicação (Configurações → «Iniciar com o
Windows»), que escreve em `HKCU` na conta certa. O instalador corre elevado, por isso criar esse
atalho a partir dele colocá-lo-ia no perfil do administrador e não no do usuário.

---

## Estrutura

```
src/NexusGuard/
  Core/       Paths · Settings · History · Logger · Shell · SystemMonitor · Pdf · Native · MVVM
  Modules/    DiskCleaner · Quarantine · Uninstaller · RegistryCleaner · Privacy · Tweaks
              Scheduler · Hardware · HealthScore · Report · SecurityManager · AppUpdater
              UpdateAgent · BackupManager · MemoryOptimizer · StartupManager
  Views/      Uma UserControl por secção + OnboardingWindow, CleanPreviewWindow, ErrorState
  Theme/      Paleta, tipografia, ícones e templates dos controlos
  Assets/     app.ico (multi-resolução) e brand/ com os PNG e o SVG da marca
installer/    NexusGuard.iss (Inno Setup) e LICENSE.txt
```

O `Assets/app.ico` é gerado a partir dos PNG em `Assets/brand/`; substituir esses arquivos muda a
identidade visual.

### Pontuação de saúde

`100 − apps×2 − drivers×4 − GB_recuperáveis/2,5 − inicialização×2 − ameaças×6 − registro/80`,
limitada a [0, 100]. Aparece no rodapé da barra lateral, com a barra colorida por faixa
(≥80 verde, ≥60 âmbar, abaixo vermelho).

### Relatório PDF

Gerado sem bibliotecas externas por um escritor de PDF próprio (`Core/Pdf.cs`): A4 claro, cabeçalho
com marca e identificação da máquina, três cartões antes/depois, tabela de ações realizadas,
recomendações e rodapé com o identificador do ponto de restauração. Fica em
`C:\ProgramData\NexusGuard\Reports`.

---

## O que precisa de administrador

Limpeza das pastas do Windows, lista de espera da memória, itens de inicialização de todos os
usuários, correções do registro em HKLM, `sfc`, `DISM`, remoção de ameaças, instalação de drivers
e atualizações do Windows, tarefas agendadas, serviços, pontos de restauração e imagem do sistema.

A app pede elevação só quando é preciso, e indica-o em cada local com um cartão de estado próprio.

---

## Limitações conhecidas

- **Assinatura de código:** o executável e o instalador não estão assinados. Sem um certificado EV
  (que tem de ser adquirido a uma autoridade certificadora), o SmartScreen avisa os primeiros
  usuários. É o passo que falta para a distribuição pública.
- **Temperaturas de CPU/GPU:** a maioria das placas não as publica no WMI. Os campos aparecem como
  indisponíveis em vez de mostrarem um valor inventado. Expô-las exigiria uma biblioteca de
  terceiros (LibreHardwareMonitor).
- **Telemetria:** o interruptor existe e fica guardado, mas não há servidor para onde enviar — nada
  sai do computador.
- **Auto-update:** não implementado; precisa de um servidor de distribuição e da assinatura acima.
- **Classificação de bloatware:** assenta numa lista de nomes conhecidos, não num serviço de
  reputação.

---

## Avisos

- O modo **espelho** do backup apaga, no destino, tudo o que já não exista na origem.
- Apagar o **Windows.old** impede voltar à versão anterior do Windows.
- Apagar o **Prefetch** torna os primeiros arranques de cada programa mais lentos.
- Depois de a quarentena expirar, a remoção é definitiva.
