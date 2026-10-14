> Manual completo preservado da entrega 0.7.1. A referência a 108 testes descreve o baseline anterior; o resultado da versão 0.7.0 é 142/142 testes lógicos. Consulte [o registro de validação](VALIDATION.md). Comandos de compilação abaixo são executados na raiz do projeto.

# Night Shift 0.7.1

**Seu projeto continua. Você descansa.** Night Shift é um aplicativo nativo para Windows x64 que acompanha sessões do Codex e do Claude Code. A interface tem um seletor de provedor sempre visível. O tema claro/escuro é independente do provedor: Codex usa azul-gelo e meia-noite; Claude usa carvão quente, creme e terracota discreta. A lateral e a identidade do Night Shift permanecem.

## Instalar e atualizar

Extraia o ZIP completo e abra `NightShift.exe`; o runtime .NET está incluído. Para atualizar uma instalação existente, feche o app, faça backup e substitua somente `NightShift.exe` na mesma pasta. Preserve `Dados`, `nightshift.portable` e o caminho da instalação para manter configurações, histórico e referências aos anexos. Uma instalação nova em outra pasta cria dados separados. O modo não portátil continua usando `%LOCALAPPDATA%/NightShift-v0.6` para preservar o caminho de dados legado.

O app usa uma instância do mesmo motor e da mesma sessão nas janelas normal e slim. O gatinho flutuante tem um ícone compacto sobre fundo cinza semitransparente com cantos arredondados e abre um painel compacto para escolher provedor/plataforma, conectar ou atualizar, ver sessões ativas, vincular uma sessão e acompanhar estado/duração. Oferece ligar/desligar turno e continuar quando essa ação é suportada. **Voltar ao app** reabre a janela principal. A janela slim não cria uma segunda tarefa ou sessão.

## Escolher e conectar um provedor

O seletor **Codex / Claude** fica no topo. A troca é permitida quando o turno está inativo; as preferências, plataforma e rascunho são próprios de cada provedor. Durante trabalho, espera, permissão ou outro turno ativo, a troca fica desabilitada com explicação. Alterar tema ou provedor não envia tarefa.

**Codex:** requer [Codex CLI instalado](https://learn.chatgpt.com/docs/cli) e autenticado. São aceitos `codex.exe` e o `codex.cmd` do npm oficial com Node no PATH; se necessário, escolha o executável em **Ações → Ajustes**. **Entrar com ChatGPT** usa o login oficial no navegador; depois desconecte e conecte novamente. O CLI gerenciado mantém conversa, streaming, histórico, permissões, anexos, comandos, limites e continuação automática do turno. O Codex mantém suas próprias credenciais e políticas.

As opções **Desktop** e **VS Code** usam o conector experimental de app-server compartilhado já acessível. Exigem que o outro cliente e Night Shift usem o mesmo servidor. Escolher uma opção não comprova que o cliente está conectado. Em **Ações → Conexão e plataformas**, informe um WebSocket local acessível, conecte, atualize a lista e vincule a sessão ao vivo. Também há tentativa pelo socket padrão, mas o proxy por socket no Windows não foi validado. O conector não descobre nem controla backends privados, não altera a configuração do Desktop/extensão e não usa cliques ou OCR.

**Claude:** requer [Claude Code local instalado](https://code.claude.com/docs/en/setup) e autenticado, com versão nativa igual ou superior a **2.1.234**. A interface não conecta ao Claude em chat normal nem ao Cowork. Não configure um cliente de chat como se fosse Claude Code. O Night Shift gerencia uma sessão local pelo protocolo estruturado oficial, com conversa, streaming, histórico, retomada, modelo, permissões e perguntas suportadas. Imagens seguem como anexos nativos quando aceitas; outros arquivos são referenciados por caminho local.

A CLI disponível varia. Recursos de listar agentes externos (`claude agents --json --all`) só são oferecidos se a versão instalada os suportar; confirme a opção com `claude agents --help`. Para sessões background externas, **Anexar** abre o terminal nativo com `claude attach <id>`. O Night Shift apenas observa estado, diretório, ID e pendências; não escreve no transcript vivo nem inicia cópia concorrente. Uma sessão interativa pode exigir resposta no terminal original. O fluxo redirecionado `claude --resume ... -p` não controla uma sessão background ainda viva e não é usado como fallback. O histórico externo só pode passar a sessão gerenciada depois que o processo anterior terminar e você confirmar explicitamente.

## Conversa, turno e limites

Use **+**, Ctrl+V ou arraste arquivos para anexar. Cada mensagem aceita até 10 arquivos de 25 MB. PNG/JPEG válidos podem ser anexados como imagem nativa. Outros arquivos são copiados para os dados do Night Shift e enviados como referência local; leitura depende das ferramentas, permissões e formato aceito pelo agente. Os originais não são alterados. Se o envio falhar, texto e anexos permanecem no campo.

O painel **Limites** e a página **Atividade e limites** mostram somente informações que o agente fornecer. No Codex, o app acompanha as janelas de cinco horas e semanal. No Claude, percentuais/reset só aparecem quando fornecidos por eventos estruturados de limite ou resultado com erro de limite; dados ausentes são **Não informado**. Não há estimativa a partir do texto da conversa.

No CLI gerenciado do Codex, o turno noturno pode continuar a tarefa interrompida após confirmar disponibilidade. O rascunho atual acompanha a continuação e só é limpo depois de aceitação, se não foi editado nesse intervalo. Sessões Codex compartilhadas exigem clique explícito em **Continuar tarefa** após revalidar a conversa. No Claude gerenciado, somente uma tarefa interrompida especificamente por limite pode ser continuada; tarefa concluída, erro comum, pergunta ou recusa nunca recebem continuação automática. Parar/fechar cancela pendências. O PC precisa permanecer ligado e conectado; **Manter o computador acordado** impede suspensão durante o acompanhamento, mas não acorda um PC suspenso.

Pedidos suportados de permissão e perguntas aparecem na interface. Não são aprovados automaticamente. A modalidade automática usa somente o modo nativo de revisão do agente quando disponível; não ativa bypass de permissões nem remove sandbox ou políticas. Se uma sessão externa tiver uma pendência que o Night Shift não possa responder, resolva-a no cliente de origem.

## Histórico, comandos e atalhos

O histórico canônico continua no agente. O cache local separa os provedores para evitar colisão de IDs e preserva a estrutura existente do Codex. No Codex CLI, **Ações → Todas as conversas** pesquisa cache e conversas do projeto; conversas extensas carregam 400 mensagens recentes e permitem expandir em lotes de 200. Exportar relatório inclui mensagens carregadas e atividade. Limpar cache local não apaga o histórico canônico.

Históricos nativos Claude muito grandes usam o último cache salvo quando disponível, com aviso de que ele pode não incluir alterações recentes feitas no terminal. Sem esse cache, o app orienta abrir o histórico no terminal original; não exibe silenciosamente uma conversa incompleta. Se um envio ficar sem confirmação, o app encerra seu trabalhador gerenciado, preserva o rascunho e pede que você confira o histórico antes de reenviar: a tarefa pode ter sido executada.

Os comandos básicos disponíveis no Claude incluem `/new`, `/resume`, `/model`, `/status`, `/stop`, `/help` e `/login`, conforme o protocolo e a versão instalada. Comandos específicos do Codex continuam no Codex; no Claude, os não suportados explicam a indisponibilidade e não executam o texto como uma tarefa. O app não aceita linhas arbitrárias no shell. Em Codex, os comandos incluem `/fork`, `/effort`, `/review`, `/compact`, `/diff`, `/mcp`, `/rename`, `/permissions`, `/approvals` e `/init`, sujeitos às limitações de cada plataforma.

Ctrl+Enter envia; Ctrl+K abre Ações; F1 abre comandos; Esc fecha o painel de limites ou interrompe o trabalho. **Parar** interrompe a sessão gerenciada selecionada. Desconectar o observador de uma sessão compartilhada não interrompe o cliente externo. Ao ler mensagens anteriores, **Ir para o fim** reativa o acompanhamento. Respostas permanecem selecionáveis; blocos de código completos têm botão Copiar.

## Compatibilidade e limites conhecidos

Alvo: Windows 10/11 x64, WPF e .NET 10 com runtime incluído. O baseline anterior foi de 108/108 testes lógicos aprovados. Não houve teste autenticado de conta real, sessão Claude real ou validação nativa em Windows nesta versão; consulte [VALIDATION.md](VALIDATION.md) para a evidência final. Desktop/VS Code não foram instalados no ambiente de investigação. Consulte [compatibilidade](COMPATIBILITY.md) para os limites do conector Codex e dos agentes externos Claude.

## Distribuição e desenvolvimento

O ZIP contém `NightShift.exe`, `nightshift.portable`, manual, compatibilidade, validação, licenças e fontes. Não inclui pasta `Dados`, binários intermediários ou credenciais. Para compilar, instale o SDK .NET 10 e execute no PowerShell:

```powershell
./scripts/Build.ps1
```

Cada build cria uma pasta única em `artifacts/build-DATA-ID` e o pacote `Night-Shift-v0.7.1-Windows-x64.zip`. Para contribuir com novos provedores, consulte [CONTRIBUTING-AGENTS.md](CONTRIBUTING-AGENTS.md). Nenhum repositório é publicado automaticamente.
