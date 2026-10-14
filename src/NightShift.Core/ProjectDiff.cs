using System.Diagnostics;
using System.Text;
namespace NightShift.Core;

public static class ProjectDiff
{
    public static async Task<string> ReadAsync(string project)
    {
        if (!Directory.Exists(project)) throw new DirectoryNotFoundException("Selecione uma pasta de projeto válida.");
        var check = await RunAsync(project, ["rev-parse", "--is-inside-work-tree"]);
        if (check.ExitCode != 0) throw new InvalidOperationException("A pasta escolhida não está em um repositório Git.");
        var sb = new StringBuilder();
        foreach (var (label, args) in new (string, string[])[] {
            ("Alterações ainda não adicionadas", ["diff", "--no-ext-diff", "--no-textconv", "--color=never", "--"]),
            ("Alterações staged", ["diff", "--cached", "--no-ext-diff", "--no-textconv", "--color=never", "--"]),
            ("Arquivos novos (nomes)", ["ls-files", "--others", "--exclude-standard"]) })
        {
            var output = await RunAsync(project, args);
            if (output.ExitCode != 0) throw new IOException("Git não conseguiu ler as alterações: " + output.Text);
            if (!string.IsNullOrWhiteSpace(output.Text)) sb.AppendLine(label + "\n" + output.Text);
        }
        return sb.Length == 0 ? "Nenhuma alteração Git encontrada." : sb.ToString();
    }
    private static async Task<(int ExitCode, string Text)> RunAsync(string project, string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Path.GetFullPath(project), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--no-pager"); start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.quotePath=false");
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Instale Git para visualizar as diferenças.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task<string> ReadBoundedAsync(StreamReader reader)
        {
            var text = new StringBuilder(); var buffer = new char[4096]; bool truncated = false;
            while (await reader.ReadAsync(buffer.AsMemory(), timeout.Token) is var count && count > 0)
            {
                var take = Math.Min(count, 24000 - text.Length); if (take > 0) text.Append(buffer, 0, take);
                if (take < count) truncated = true;
            }
            if (truncated) text.Append("\n[Saída abreviada. Consulte o Git para o diff completo.]"); return text.ToString();
        }
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput); var stderr = ReadBoundedAsync(process.StandardError);
            await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(stdout, stderr);
            return (process.ExitCode, await stdout + await stderr);
        }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw new TimeoutException("Git demorou demais para responder."); }
    }
}
