# Contribuir com provedores de agentes

Night Shift oferece uma interface com provedores explícitos. Para adicionar outro agente, dê a ele um valor próprio em `AgentProvider`, um localizador de instalação/autenticação, um adaptador e uma opção de criação na fábrica usada pelo `MainViewModel`; exponha o rótulo e as capacidades específicas na UI. O arquivo `MainViewModel.Providers.cs` mostra como o view model seleciona provedores, guarda preferências e troca adaptadores. Não represente serviços distintos como um provedor genérico “Other” nem anuncie compatibilidade universal: cada integração deve documentar mecanismo oficial, capacidades verificadas, versão mínima, limitações e segurança.

## Adaptador de sessão

Implemente `IAgentSession` em `src/NightShift.Core/IAgentSession.cs`, seguindo `CodexSession` como superfície consumida pelo `MainViewModel`. Acrescente a criação do adaptador na fábrica de sessões injetada no `MainViewModel` (consulte `MainViewModel.Providers.cs`) e conecte as opções e rótulos da UI às capacidades reais do agente. Preserve a identidade do provedor em cada sessão e isole o cache por provedor; IDs iguais em backends distintos não podem carregar ou sobrescrever a conversa errada. Não altere o hash ou a localização histórica das sessões Codex existentes.

Mantenha um único trabalhador por sessão. Faça troca de provedor somente com turno inativo e nunca inicie trabalho como efeito colateral de troca de provedor, plataforma ou tema. Desconectar um observador externo não deve parar o processo do cliente externo. Não dirija a interface por cliques ou OCR.

## Protocolo e segurança

Use mecanismos estruturados documentados pelo fornecedor. Separe erro de limite, conclusão normal, pergunta, recusa e erro comum; somente sinais de limite estruturados autorizam o fluxo de continuação. Se reset ou percentuais não forem fornecidos, mostre **Não informado**. Cancelamento e fechamento devem liberar tarefas pendentes.

Não execute comandos de usuário no shell, não contorne permissões, sandbox ou política de revisão. Ações não suportadas devem ser desabilitadas ou explicadas sem reinterpretar o texto como tarefa. Para sessões externas, observar metadados não concede controle do processo nem autorização para escrever em históricos vivos. Exija confirmação explícita antes de converter histórico externo em sessão gerenciada.

## Documentação e validação

Atualize `docs/COMPATIBILITY.md` para descrever os mecanismos, versões e limites realmente verificados. Diferencie testes simulados de uso autenticado e de teste nativo Windows; nunca transforme ausência de evidência em alegação de compatibilidade. Registre comandos e resultados reproduzíveis em `docs/VALIDATION.md`.

Adicione testes para eventos malformados e fragmentados, Unicode, desconexão, cancelamento, limite versus erro comum, colisão de IDs entre provedores, preservação de rascunho e ausência de takeover em sessão externa. Use testes de protocolo simulados para caminhos determinísticos; não alegue validação real de conta ou cliente sem executá-la.
