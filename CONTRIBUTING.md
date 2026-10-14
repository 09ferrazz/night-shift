# Contribuir com o Night Shift

Obrigado por ajudar a tornar o acompanhamento de sessões mais claro e confiável. O projeto usa C#, WPF e .NET 10, com Windows x64 como alvo.

## Relatar um problema

Abra uma issue com a versão do Night Shift, versão da CLI, provedor/plataforma, passos para reproduzir, resultado esperado e resultado observado. Diferencie sessão gerenciada de sessão externa e informe se o problema depende de tema, modo slim ou escala de DPI.

Inclua apenas logs e capturas revisados por você. Remova tokens, identificadores sensíveis, nomes de usuário, caminhos privados, conteúdo de projetos e anexos pessoais. Para vulnerabilidades, siga [SECURITY.md](SECURITY.md).

## Preparar uma alteração

1. Faça um fork ou crie um branch de trabalho.
2. Mantenha a mudança focada e descreva o problema que resolve.
3. Atualize testes quando houver mudança de comportamento e documentação quando houver mudança visível ou de compatibilidade.
4. No Windows com SDK .NET 10, execute na raiz:

```powershell
./scripts/Build.ps1 -ValidateUI
```

O script executa lógica e WPF, publica o app e cria um pacote numa pasta única. Para testar só a lógica sem publicar:

```powershell
dotnet run --project ./tests/NightShift.Tests/NightShift.Tests.csproj -c Release
```

No pull request, registre comandos, resultados e o que não foi verificado. Testes simulados não comprovam login real ou compatibilidade ponta a ponta; compilar a suíte WPF não comprova execução nativa.

## Mapa do projeto

| Caminho | Responsabilidade |
| --- | --- |
| `src/NightShift.App` | Interface WPF, viewmodel e janelas principal/slim |
| `src/NightShift.Core` | Sessões, protocolos, persistência e modelos |
| `src/NightShift.Core/IAgentSession.cs` | Contrato das capacidades de uma sessão |
| `tests/NightShift.Tests` | Testes de lógica e fixtures de protocolo |
| `tests/NightShift.Windows.Tests` | Verificações nativas WPF |
| `scripts/Build.ps1` | Testes, publicação e empacotamento |
| `docs` | Manual, compatibilidade, validação e avisos de terceiros |

## Regras que preservam a confiança

- Mantenha rascunhos e caches separados por provedor e preserve os caminhos históricos Codex.
- Não inicie tarefa ao trocar provedor, plataforma ou tema.
- Preserve um único motor compartilhado entre janela principal e slim.
- Não aprove permissões automaticamente nem contorne sandbox ou políticas.
- Não escreva em transcripts externos vivos nem assuma o controle de processos de outros clientes.
- Diferencie limite estruturado, conclusão, erro, pergunta e recusa antes de continuar uma tarefa.
- Não acrescente credenciais, dados pessoais, `Dados`, binários intermediários ou anexos ao repositório.

Para novos provedores, leia [CONTRIBUTING-AGENTS.md](docs/CONTRIBUTING-AGENTS.md). Documente somente capacidades verificadas e mantenha limitações explícitas em [COMPATIBILITY.md](docs/COMPATIBILITY.md).
