# Compatibilidade — Night Shift 0.7.1

Night Shift oferece integração gerenciada com Codex CLI e Claude Code local. A compatibilidade depende da CLI e das capacidades estruturadas instaladas; seleção de provedor/plataforma não equivale a detectar ou controlar qualquer aplicativo com nome semelhante.

## Codex CLI gerenciado

O CLI do Codex deve estar instalado e autenticado. O Night Shift usa protocolo oficial para conversa, histórico, permissões, anexos, limites e continuação. O baseline existente foi 108/108 testes lógicos aprovados. Isso não representa teste autenticado de conta real nesta versão. Credenciais e políticas continuam sob responsabilidade do Codex.

## Codex Desktop e VS Code

Os conectores experimentais usam um app-server compartilhado local já acessível ao outro cliente. Desktop e extensão VS Code não foram instalados no ambiente de investigação. A documentação consultada não fornece contrato universal para controlar qualquer janela ou backend privado; sincronizar histórico entre clientes não garante acesso à sessão viva.

A inspeção anterior com Codex CLI 0.158.0 verificou WebSocket local: handshake, listagem de sessões carregadas, assinatura de eventos e interrupção; desconectar o observador não encerra o trabalho do outro cliente. O caminho por proxy de socket no Windows expirou durante `initialize`, sem causa estabelecida, e continua não validado. O seletor de plataforma é uma escolha do usuário, não uma detecção do cliente atual. O campo de origem indica a criação da conversa.

A retomada compartilhada após limite exige ação explícita e revalidação, pois o protocolo instalado não oferece operação atômica para garantir ausência de turno iniciado por outro cliente. Não há continuação compartilhada automática. Solicitações só podem ser respondidas quando o servidor realmente as encaminha; caso contrário, devem ser resolvidas no cliente de origem. Endpoints remotos, WSL, token, descoberta de portas privadas, bancos privados e automação de UI não são suportados.

## Claude Code

A integração gerenciada requer Claude Code local instalado e autenticado; a versão nativa mínima exigida é **2.1.234**. Não é integração com Claude em chat normal, Claude Desktop ou Cowork. O adaptador usa o protocolo estruturado do Claude Code para entrada/saída, histórico, retomada, modelo, anexos de imagem quando aceitos, permissões e perguntas suportadas. Não usa `bypassPermissions`; modo automático utiliza somente a modalidade nativa de revisão quando disponível.

Limites são apresentados somente quando há `rate_limit_event` estruturado ou resultado com erro de limite. Percentuais e reset ausentes são **Não informado**. Apenas tarefa interrompida por limite pode entrar em continuação; erro comum, conclusão, pergunta e recusa não retomam automaticamente. Comandos específicos do Codex ficam indisponíveis no Claude com explicação e não são reinterpretados como tarefa.

O CLI pode variar entre instalações. A listagem de agentes/sessões externos depende do suporte da versão instalada a `claude agents --json --all`; confirme a capacidade com `claude agents --help`. Para sessão background suportada, `claude attach <id>` abre o terminal nativo e observa sem escrever no transcript vivo. Sessões interativas podem exigir resposta no terminal original. Um `--resume -p` redirecionado não controla sessão background que ainda vive; não é fallback. O histórico externo só pode ser retomado como sessão gerenciada após o processo anterior terminar e confirmação explícita.

A leitura de um histórico nativo tem limites de 64 MB por arquivo e 8 MB por registro. Imagens codificadas no histórico podem exceder esses limites. Nessa situação, um cache local existente preserva o texto e os anexos salvos pelo Night Shift, com aviso explícito de que pode estar desatualizado; sem cache, a conversa deve ser consultada no terminal original. O aplicativo não descarta registros grandes silenciosamente. A listagem lê apenas prévias limitadas, sem carregar todas as conversas.

Se a confirmação de um envio não chegar, o estado é tratado como incerto: o trabalhador criado pelo Night Shift é encerrado, a continuação é cancelada e o rascunho permanece. Confira o histórico antes de reconectar e reenviar, pois o agente pode já ter executado a tarefa. Processos externos não são encerrados.

A validação lógica não comprova login real, execução de uma sessão Claude real, disponibilidade de subcomandos em todas as versões ou comportamento nativo da janela no Windows. Essas verificações devem ser registradas em `docs/VALIDATION.md` somente quando executadas.

## Fontes oficiais

- [Codex App Server](https://learn.chatgpt.com/docs/app-server): transporte, conversas e eventos.
- [Configuração da extensão Codex](https://learn.chatgpt.com/docs/ide/settings): `chatgpt.cliExecutable` é opção de desenvolvimento; Night Shift não a altera.
- [Claude Code CLI](https://code.claude.com/docs/en/cli-reference): referência de comandos.
- [Claude Code headless](https://code.claude.com/docs/en/headless): entrada e saída estruturadas.
- [Retomar sessão background Claude](https://code.claude.com/docs/en/sessions#resume-a-running-background-session): distinção entre attach e retomada de histórico.
- [Hooks Claude](https://code.claude.com/docs/en/hooks): eventos e automações.

A existência de uma API oficial permite integrar os recursos documentados, mas não comprova compatibilidade ponta a ponta com clientes ausentes ou versões não verificadas.
