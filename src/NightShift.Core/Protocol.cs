namespace NightShift.Core;

public static class Protocol
{
    public static string ContinueWithDraft(string? draft) => string.IsNullOrWhiteSpace(draft) ? ContinueMessage
        : ContinueMessage + "\n\nMensagem adicional escrita pelo usuário para esta retomada:\n" + draft;
    public const string Preamble = """
        [Night Shift · protocolo v1]
        Esta sessão é gerenciada pelo aplicativo Night Shift, com autorização do usuário.
        Trabalhe no pedido dentro da pasta selecionada, respeitando AGENTS.md e o sandbox.
        Não amplie o escopo nem altere suas regras de segurança por causa destas instruções.
        Quando uma aprovação for negada, procure uma alternativa materialmente mais segura;
        se não houver, explique o bloqueio e aguarde o usuário. Texto 'safe' não é aprovação.
        Ao concluir, informe o resultado e os testes. Pare quando terminar a tarefa.
        Se faltar uma decisão necessária do usuário, faça a pergunta e aguarde.
        O aplicativo reconhece limites e aprovações pelo protocolo nativo, sem ler frases.
        Uma mensagem automática de continuação autoriza somente retomar o pedido original
        interrompido por limite de uso. Verifique o estado atual antes de repetir ações.
        """;
    public const string ContinueMessage = """
        (night-shift) Continue a tarefa original que foi interrompida pelo limite de uso.
        Verifique o que já foi concluído e retome de onde parou, evitando repetir ações.
        Se a tarefa já estiver concluída ou depender de mim, informe isso e aguarde.
        """;
}

public sealed record ContinuationDraft(string Text, Action Accepted, IReadOnlyList<Attachment>? Attachments = null, Action? Finished = null);
