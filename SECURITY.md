# Segurança

Night Shift integra CLIs locais e preserva as políticas dos provedores. Não aprova pedidos de permissão automaticamente, não ativa bypass de permissões e não remove sandbox. Observar uma sessão externa não concede controle de seu processo ou autorização para escrever no histórico vivo.

## Relatar uma vulnerabilidade

Não publique detalhes exploráveis, credenciais ou dados privados em uma issue pública.

Se a página **Security** deste repositório oferecer **Report a vulnerability**, use esse canal privado. Inclua versão afetada, provedor/plataforma, passos de reprodução, impacto e uma demonstração mínima sem dados pessoais.

A disponibilidade desse canal depende da configuração do repositório. Se ele não estiver habilitado, abra apenas uma solicitação pública para habilitar o relato privado, **sem detalhes da vulnerabilidade**. Não há endereço de contato, prazo de resposta ou janela de suporte presumidos neste documento.

**Para o mantenedor:** habilite o recurso de relato privado de vulnerabilidades nas configurações de segurança do GitHub antes de convidar relatos confidenciais.

## Revisar logs e capturas antes de compartilhar

Remova tokens, cookies, códigos de login, cabeçalhos de autenticação, endpoints com segredos, caminhos locais sensíveis, nomes de usuário, conteúdo de tarefas e anexos. IDs de sessão e de projeto também podem expor contexto privado. Substitua valores por exemplos consistentes que preservem a reprodução.

Não envie a pasta `Dados`, backups ou históricos completos. Verifique também metadados e texto visível em capturas. As credenciais do Codex e do Claude Code devem permanecer sob controle de suas próprias CLIs.

## Limites relevantes

- Codex compartilhado depende de um backend local já acessível e exige confirmação para continuar.
- Claude externo é observado; pendências devem ser resolvidas na origem quando necessário.
- Um envio sem confirmação pode ter sido executado. Confira o histórico antes de reenviar.
- A evidência de testes de protocolo simulados não substitui validação autenticada ou execução nativa no Windows.

Veja [compatibilidade](docs/COMPATIBILITY.md) e [validação](docs/VALIDATION.md) para o alcance exato das verificações registradas.
