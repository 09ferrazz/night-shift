# Night Shift — protocolo v1

## Princípio
Eventos de máquina vêm do App Server oficial, por JSON-RPC sobre stdio. Não
interpretar variações de texto livre para limites, permissões ou fim de tarefa.
A identidade do pedido é o `id` JSON original; mantê-lo ao responder, inclusive
quando for string. Versão de referência de esquema: Codex CLI 0.160.1.

## Eventos utilizados

| Evento | Comportamento |
| --- | --- |
| `turn/started` | Exibir trabalhando e memorizar o turno |
| `item/agentMessage/delta` | Atualizar mensagem incremental |
| `item/completed` | Usar o texto final como fonte autoritativa |
| `turn/completed` com `usageLimitExceeded` | Aguardar quota e preparar retomada |
| `account/rateLimits/updated` | Atualizar consumo e horário de reset |
| `item/autoApprovalReview/started` | Mostrar revisão em andamento |
| `item/autoApprovalReview/completed` | Registrar status e motivo do revisor |
| Pedido com `id` | Exibir solicitação; jamais inferir aprovação pelo texto |
| `serverRequest/resolved` | Remover solicitação resolvida |

## Estados do supervisor

Na versão 0.4, a continuação também pode incluir o rascunho escrito pelo usuário. Ele é capturado após a consulta de disponibilidade, imediatamente antes do envio, e anexado sob uma identificação explícita de mensagem adicional. Apenas uma resposta aceita de `turn/start` permite limpar esse rascunho; edições concorrentes são preservadas por revisão. O rascunho não é executado como comando local. A confirmação manual permanece obrigatória no conector compartilhado. A existência do rascunho não altera os critérios de limite, permissão, prazo ou parada.

Na versão 0.5, a continuação inclui também os anexos que estiverem no campo no momento do envio. Imagens validadas entram como `localImage` no `turn/start`; outros arquivos são referenciados por caminho na mensagem. Só uma resposta aceita remove os anexos enviados do campo. Anexos adicionados durante a tentativa permanecem no rascunho.

Na versão 0.6, mensagens enviadas enquanto há um turno ativo usam `turn/steer` com `expectedTurnId`. Uma resposta aceita limpa somente o rascunho enviado e não desarma o turno noturno. Uma rejeição mantém texto e anexos para nova tentativa. Pedidos de permissão pendentes e operações manuais não aceitam direção nessa interface.

Desconectado → Pronto → Trabalhando → Concluído, Precisa de você ou Aguardando limite.
O turno é uma condição independente: somente um turno armado e ainda no prazo
pode iniciar a continuação automática. Parar/desconectar/fechar desarma o turno.
Concluído não inicia outro pedido. Falhas comuns não são confundidas com quota.

`account/rateLimits/read` confirma disponibilidade antes de continuar. Esperar a
última liberação das janelas esgotadas. Acrescentar margem de 15 segundos; reset
vencido/dados desconhecidos usam nova consulta em cinco minutos.

## Instruções fornecidas ao Codex

Trabalhar apenas na tarefa e pasta autorizadas; preservar AGENTS.md e políticas.
Se a revisão negar, procurar uma forma materialmente mais segura ou aguardar.
Ao concluir, relatar resultado/testes e parar. Perguntas necessárias aguardam o usuário.
A continuação automática somente retoma uma tarefa interrompida por quota, verificando
o estado para evitar repetições. Nenhuma frase pode conceder permissões.

## Mensagem de retomada

> (night-shift) Continue a tarefa original que foi interrompida pelo limite de uso.
> Verifique o que já foi concluído e retome de onde parou, evitando repetir ações.
> Se a tarefa já estiver concluída ou depender de mim, informe isso e aguarde.

Fonte do protocolo externo: https://learn.chatgpt.com/docs/app-server
