# Preparação do repositório GitHub — 14/10/2026

- Fontes do aplicativo e testes preservados em relação ao ZIP original 0.7.1.
- **142/142 testes de lógica aprovados novamente**, exit 0, com o comando adotado pelo CI:

```text
dotnet run --project tests/NightShift.Tests/NightShift.Tests.csproj -c Release -p:UseAppHost=false -p:BuildInParallel=false -p:UseSharedCompilation=false
```

- Executado em Linux com SDK .NET 10.0.100. Os testes usam servidores e subprocessos simulados.
- O executável distribuído é o da entrega original, sem reconstrução nesta preparação.
- Novos README, imagens explicativas, licença MIT, guias de contribuição/segurança e configurações GitHub. `Build.ps1` alterado apenas para incluir a documentação e os metadados novos no pacote.
- Workflows configurados para testes de lógica em Linux e compilação WPF em Windows. Não executados no GitHub nesta preparação; dependem de publicar o repositório.
- PowerShell indisponível neste ambiente: o script de empacotamento modificado não foi executado aqui. A interface WPF, os fluxos autenticados e a retomada após limites reais continuam pendentes de validação no Windows.

---

# Validação — Night Shift 0.7.1

13/10/2026. Ajuste visual localizado no ícone flutuante do slim.

- Janela/ícone reduzido de 76 para 64 px; imagem do gatinho com 54 px, centralizada.
- Fundo cinza semitransparente, contorno discreto e cantos de 16 px. A indicação de foco do teclado continua visível.
- Janela, botão e cálculo de posição usam o mesmo tamanho; a linha do ícone acompanha sua altura automaticamente.
- Handlers de clique e arraste, painel, sessões, permissões e integrações foram preservados.
- `NightShift.Windows.Tests` compilado em Release com zero erros e três avisos CS8602 preexistentes. As duas verificações antigas de tamanho foram atualizadas para usar a constante compartilhada; nenhum teste novo foi necessário para esta mudança visual.
- A suíte de 142 testes de lógica passou na versão-base 0.7.0, conforme o registro abaixo; não foi repetida nesta alteração de aparência.
- Publicação Release concluída (exit 0): executável único Windows x64 com runtime .NET 10.0.12 incluído.
- A validação nativa da interface no Windows continua pendente: este ambiente é Linux. Compilar a suíte WPF não equivale a executá-la.

---

# Validação — Night Shift 0.7.0

13/10/2026. Integração Claude Code, seletor com temas por provedor e janela slim.

## Verificado nesta entrega

- Baseline preservado: os 108 testes existentes continuam na suíte. Resultado final independente em Release: **142/142 testes de lógica aprovados**, exit 0.
- Cobertura nova: protocolo Claude com chunks/Unicode, permissões e perguntas, limites estruturados, cancelamento, cache separado por provedor, troca de IA sem envio, rascunhos/anexos independentes, descoberta externa sem assumir a sessão, slim e posicionamento.
- Regressões reproduzidas antes da correção: reconexão Codex por WebSocket sem CLI local; ativação do turno no slim com sessão compartilhada ociosa; histórico Claude com imagens grandes; confirmação de envio expirada; sessão Claude interativa sem identificador exclusivo de background.
- Revisão independente: nenhum achado Critical/Important pendente após as correções. O slim compartilha o motor e o viewmodel com a janela principal.
- `NightShift.Windows.Tests` compilado em Release: **zero erros**, três avisos CS8602 preexistentes nos testes AppearanceTests e AttachmentUiTests. Uma tentativa anterior sem restaurar os pacotes falhou por falta do targeting pack; a restauração e compilação completas passaram.
- `NightShift.exe` publicado em Release para **Windows x64**, self-contained, arquivo único, runtime **.NET 10.0.12**, SDK **10.0.100**. Publicação com exit 0; cabeçalho PE x64 conferido e pasta de publicação contendo somente o executável.
- `git diff --check` passou. Fontes, documentação e licenças acompanham o pacote; a distribuição exclui Dados, credenciais, bin/obj e metadados internos de desenvolvimento.

## Limites da validação

Este ambiente é Linux. **A interface WPF e a suíte Windows não foram executadas.** Renderização, teclado, foco, arraste, múltiplos monitores, escalas de DPI, diálogos do slim e abertura do terminal nativo ainda precisam ser conferidos no Windows. Compilar esses testes não equivale a executá-los.

As verificações de Codex/Claude usam subprocessos e servidores simulados com os formatos nativos. Não houve login autenticado, conversa real com Claude, esgotamento real de limites ou controle de Desktop/VS Code nesta rodada. O pacote não deve ser entendido como validação ponta a ponta desses clientes.

Claude externo é somente observação, com abertura do terminal original para sessões background suportadas. Codex externo requer o mesmo backend compartilhado acessível e confirmação explícita para continuar após limite. Retomada automática Claude vale para sessões gerenciadas pelo Night Shift, quando um limite estruturado interrompe a tarefa. Consulte COMPATIBILITY.md.

Históricos nativos Claude acima dos limites de leitura usam cache salvo com aviso explícito de possível desatualização; sem cache, orientam consulta no terminal original. Um envio sem confirmação pode já ter sido executado: o trabalhador gerenciado é encerrado, o rascunho permanece e o histórico deve ser conferido antes de reenviar.

## Reprodução e atualização

No Windows com SDK .NET 10, execute `./scripts/Build.ps1 -ValidateUI` dentro de Codigo-fonte para rodar a suíte de lógica, os testes WPF e gerar um novo pacote.

Para atualizar, feche o app, faça backup e substitua somente NightShift.exe na pasta atual. Preserve Dados, nightshift.portable e o caminho da instalação. Conversas Claude têm cache separado; os caminhos legados do Codex permanecem compatíveis.

---

# Validação — Night Shift 0.6.2

12/10/2026. Ajuste localizado da busca lateral e retorno de ícones de traço fino.

- `NightShift.Tests`: **108/108** testes aprovados após a mudança.
- `NightShift.Windows.Tests`: compilado com zero erros. Permanecem três avisos
  CS8602 já existentes nas verificações dos controles.
- O teste de busca deixou de checar somente a altura do host: agora confere o
  retângulo real do caractere digitado, seu centro vertical e recorte.
- Esse teste WPF **não foi executado**, pois esta máquina é Linux.
- A implementação utiliza um template exclusivo para SidebarSearch; o compositor,
  mensagens e campos multilinha continuam usando seus templates anteriores.
- Ícones vetoriais são ContentTemplates. Handlers, bindings e nomes de controles
  permanecem iguais. Recursos e XML foram verificados.
- Núcleo de sessões, viewmodels, conexões, permissões, limites e dados sem alterações.
- Executável win-x64 self-contained, arquivo único, .NET 10.0.12 incluído.

A confirmação visual do alinhamento desta versão precisa ser feita no Windows.
O relato e a captura do usuário confirmaram o problema visual na versão 0.6.1.
Nenhuma chamada autenticada ao Codex real foi feita durante estes ajustes.

---

# Validação — Night Shift 0.6.1

12/10/2026. Redesign realizado a partir do ZIP v0.6 fornecido pelo usuário.

## Verificado nesta entrega

- Baseline antes de editar: **108/108** testes de `NightShift.Tests` passaram.
- Após o redesign: **108/108** testes passaram, com a mesma suíte e sem remoção de casos.
- Compilação Release e publicação de `NightShift.exe`, Windows x64, self-contained,
  arquivo único, runtime **10.0.12**, SDK **10.0.100**: concluídas.
- `NightShift.Windows.Tests` compilado em Release: zero erros e três avisos CS8602
  preexistentes em AppearanceTests.cs e AttachmentUiTests.cs.
- Os 21 arquivos de Core e MainViewModel permanecem byte a byte iguais aos do ZIP original.
- Nenhum controle XAML nomeado nem handler de eventos existente foi removido.
  A ação duplicada de abrir o menu da conversa foi removida; ela permanece no topo e em Ctrl+K.
- O seletor de plataforma mantém seus bindings e regras de habilitação, agora em Conexão.
- Recursos XAML referenciados resolvidos; XML bem formado; caminhos PNG/ICO válidos.
- Logo PNG com transparência e ICO com tamanhos de 16 a 256 px.
- Contraste de texto principal, secundário, botões e avisos conferido nos dois temas.
  Corrigido o contraste do texto secundário no estado pressionado do tema claro (4,73:1).
- Revisão independente de código: nenhuma regressão funcional concreta alta/média identificada.

## Limites da validação

O ambiente desta entrega é Linux. **Não foi executada a janela WPF em Windows**;
a abertura do executável, renderização, teclado/clipboard e escalas de 125%/150%
precisam ser conferidos em Windows. Compilar testes WPF não equivale a executá-los.
A tentativa inicial de compilar os testes WPF simultaneamente com outra build falhou
sem diagnóstico de compilador; a execução serial (-m:1) completou com zero erros.

Os testes de lógica usam um servidor simulado. Não houve teste autenticado real
com Codex, esgotamento real de limites nem conexão real com Desktop/VS Code nesta rodada.
As limitações dos conectores descritas em COMPATIBILITY.md continuam válidas.
A evidência histórica da versão recebida foi preservada em VALIDATION-v0.6.md;
não deve ser interpretada como execução nativa do redesign.

## Reprodução no Windows

Com SDK .NET 10, dentro de Codigo-fonte:

```powershell
./scripts/Build.ps1 -ValidateUI
```

O script roda os testes de lógica, executa a suíte WPF e suas capturas nativas,
publica o executável e cria um ZIP em uma nova pasta de artifacts.

## Dados existentes

Para atualizar, feche o aplicativo e substitua apenas NightShift.exe na pasta atual,
preservando Dados, nightshift.portable e o caminho da instalação. A rotina de migração
original não foi alterada; ela importa automaticamente apenas a v0.5 adjacente.
O pacote de distribuição não contém dados pessoais, históricos, credenciais ou anexos.
