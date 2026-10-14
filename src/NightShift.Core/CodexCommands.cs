using System.Text.RegularExpressions;
namespace NightShift.Core;

public sealed record CommandDefinition(string Name, string Syntax, string Title, string Description, string VisualCommand);
public sealed record ParsedCommand(bool IsCommand, CommandDefinition? Definition = null, string Argument = "", string? Error = null);

public static class CodexCommands
{
    public static IReadOnlyList<CommandDefinition> All { get; } = Array.AsReadOnly(new CommandDefinition[] {
        new("new", "/new", "Nova conversa", "Comece outra tarefa na pasta escolhida.", "/new"),
        new("resume", "/resume [--last | ID]", "Retomar conversa", "Abra o histórico original do CLI. --last retoma a última do projeto.", "/resume"),
        new("fork", "/fork [--last | ID]", "Criar cópia da conversa", "Continue por outro caminho em uma nova conversa com o mesmo histórico.", "/fork"),
        new("model", "/model [modelo]", "Escolher modelo", "Consulte e selecione os modelos disponíveis na sua instalação.", "/model"),
        new("effort", "/effort [nível]", "Esforço de raciocínio", "Escolha o esforço oferecido pelo modelo selecionado.", "/effort"),
        new("status", "/status", "Estado da sessão", "Confira pasta, conversa, modelo, limites e duração do turno.", "/status"),
        new("review", "/review", "Revisar alterações", "Peça ao revisor do Codex para examinar alterações ainda não commitadas.", "/review"),
        new("compact", "/compact", "Compactar contexto", "Resuma o contexto da conversa pelo recurso nativo do Codex.", "/compact"),
        new("diff", "/diff", "Ver alterações Git", "Confira diferenças staged, unstaged e nomes de arquivos novos.", "/diff"),
        new("mcp", "/mcp", "Ver integrações MCP", "Consulte as conexões configuradas no seu Codex local.", "/mcp"),
        new("rename", "/rename nome", "Renomear conversa", "Dê um nome fácil de encontrar no histórico do Codex.", "/rename "),
        new("permissions", "/permissions", "Permissões", "Abra as opções de revisão automática e aprovação manual.", "/permissions"),
        new("login", "/login", "Entrar com ChatGPT", "Abra o login oficial do Codex no navegador.", "/login"),
        new("init", "/init", "Preparar instruções do projeto", "Peça ao Codex para criar ou melhorar o AGENTS.md do projeto.", "/init"),
        new("stop", "/stop", "Parar trabalho", "Interrompa a geração e cancele o agendamento noturno.", "/stop"),
        new("help", "/help", "Ajuda e comandos", "Abra esta lista de atalhos e ações visuais.", "/help"),
        new("quit", "/quit", "Fechar Night Shift", "Encerre o aplicativo e sua sessão gerenciada.", "/quit")
    });
    private static readonly IReadOnlyDictionary<string, CommandDefinition> Definitions = All.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase) { ["exit"] = "quit", ["clear"] = "new", ["approvals"] = "permissions" };
    public static ParsedCommand Parse(string text)
    {
        var input = text.Trim();
        if (input.Equals("codex", StringComparison.OrdinalIgnoreCase)) input = "/new";
        else if (input.StartsWith("codex ", StringComparison.OrdinalIgnoreCase)) input = "/" + input[6..].TrimStart();
        if (!input.StartsWith('/')) return new(false);
        var split = input.IndexOfAny([' ', '\t', '\r', '\n']);
        var name = input[1..(split < 0 ? input.Length : split)];
        var argument = split < 0 ? "" : input[(split + 1)..].Trim();
        if (name.Equals("--resume", StringComparison.OrdinalIgnoreCase)) name = "resume";
        if (Aliases.TryGetValue(name, out var alias)) name = alias;
        if (!Definitions.TryGetValue(name, out var definition))
            return new(true, Error: $"Comando não disponível: /{name}. Abra Comandos ou use /help para ver os atalhos suportados.");
        var valid = definition.Name switch {
            "resume" or "fork" => argument is "" or "--last" || !argument.StartsWith('-') && Regex.IsMatch(argument, "^[A-Za-z0-9_-]+$"),
            "model" or "effort" => argument == "" || Regex.IsMatch(argument, "^[A-Za-z0-9_.:/-]+$") && !argument.StartsWith('-'),
            "rename" => argument.Length is > 0 and <= 160 && !argument.Contains('\n') && !argument.Contains('\r'),
            _ => argument == ""
        };
        return valid ? new(true, definition, argument) : new(true, definition, argument, "Use " + definition.Syntax + ". Essa opção não é suportada pelo Night Shift.");
    }
}
