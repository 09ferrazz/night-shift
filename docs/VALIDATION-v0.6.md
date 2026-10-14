# Validação — Night Shift 0.6

11/10/2026, Windows x64, .NET 10. Desenvolvimento em cópia isolada da v0.5. A v0.1 em execução e as versões anteriores não foram substituídas.

## Testes da versão

- A suíte `NightShift.Tests` verifica a lógica de sessões, limites, permissões, retomada, anexos, histórico, migração portátil e `turn/steer` com um app-server simulado. O caso de direção durante o turno confirma que o Night Shift permanece ativo, não inicia um segundo turno, mostra a mensagem uma vez e preserva o rascunho quando o servidor rejeita o envio. Outro caso mantém o campo bloqueado enquanto há uma permissão pendente.
- `NightShift.Windows.Tests` abriu controles WPF reais. Verificou a busca digitada e legível, filtro de projeto, espaçamento da lateral, Voltar entre páginas e no painel de limites, comando de colar imagem, colagem de texto comum, temas claro/escuro, layouts de 760 a 1280 px, escalas de 125% e 150%, quotas, permissões e rolagem de respostas grandes. Não houve erro de binding WPF.
- O código gerou o esquema experimental do Codex CLI 0.158.0 instalado com `codex app-server generate-json-schema --experimental`. Ele contém `turn/steer` e `expectedTurnId`, conforme a [documentação oficial](https://learn.chatgpt.com/docs/app-server#steer-an-active-turn).

Os testes de `turn/steer` e de colagem de imagem usam servidor/clipboard simulados. O esquema foi verificado no Codex instalado, mas não houve um turno autenticado real para exercitar o envio durante a geração. O aplicativo Codex Desktop e a extensão Codex no VS Code não estão instalados nesta máquina; seus conectores continuam experimentais e dependem do mesmo servidor compartilhado. Não houve esgotamento real de limite nem pedido real de permissão.

## Executável

O script `scripts/Build.ps1` roda as suítes, publica um `NightShift.exe` portátil independente, inclui código-fonte, documentação e licenças e gera ZIP. O pacote entregue não contém `Dados`. O teste de abertura do executável é feito em uma cópia de staging, sem importar conversas particulares da instalação anterior para o pacote público.
