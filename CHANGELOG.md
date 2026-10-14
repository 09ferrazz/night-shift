# Histórico de versões

Este resumo acompanha o registro de [validação](docs/VALIDATION.md). Resultados históricos não equivalem a uma nova execução nesta preparação do repositório.

## 0.7.1 — 13/10/2026

- Ícone flutuante do modo slim reduzido de 76 para 64 px, com imagem centralizada de 54 px.
- Fundo cinza semitransparente, contorno discreto e cantos de 16 px; indicação de foco preservada.
- Janela, botão e cálculo de posição usam o mesmo tamanho compartilhado.
- Handlers, painel, sessões, permissões e integrações preservados.
- Suíte Windows compilada em Release sem erros, com três avisos CS8602 preexistentes; publicação Release concluída.
- Suíte lógica de 142 testes aprovada na base 0.7.0, sem repetição neste patch visual. Validação nativa Windows pendente.

## 0.7.0 — 13/10/2026

- Integração gerenciada com Claude Code local pelo protocolo estruturado, além do Codex CLI.
- Seletor de provedor, identidade visual por provedor e temas claro/escuro independentes.
- Preferências, rascunhos, anexos e cache isolados por provedor.
- Janela slim compartilhando o motor e o viewmodel da janela principal.
- Observação de sessões Claude externas com abertura do terminal para background suportado, sem takeover.
- Continuação Claude restrita a interrupções estruturadas por limite; Codex compartilhado exige confirmação explícita.
- Correções para reconexão Codex por WebSocket, turno slim compartilhado, histórico Claude com imagens grandes e confirmação de envio expirada.
- **142/142 testes de lógica aprovados** com fixtures; publicação Windows x64 concluída. UI Windows e fluxos autenticados reais não executados.

## 0.6.2 — 12/10/2026

- Ajuste localizado do campo de busca lateral e retorno de ícones de traço fino.
- Teste de busca passou a verificar o retângulo real do caractere, centro vertical e recorte.
- Núcleo de sessões, conexões, permissões, limites e dados preservado.
- **108/108 testes de lógica aprovados**; suíte WPF compilada sem erros e com três avisos preexistentes.
- Confirmação visual no Windows pendente; teste WPF e chamadas autenticadas reais não executados.
