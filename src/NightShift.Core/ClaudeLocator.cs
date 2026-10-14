using System.Diagnostics;
namespace NightShift.Core;

public static class ClaudeLocator
{
    public static string? Find(string? configured)
    {
        if(!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))return Path.GetFullPath(configured);
        var directories=(Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".local","bin"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"npm"));
        var names=OperatingSystem.IsWindows()?new[]{"claude.exe","claude.cmd"}:new[]{"claude"};
        foreach(var name in names)foreach(var directory in directories)
        {
            var path=Path.Combine(directory.Trim('"'),name);
            if(File.Exists(path))return Path.GetFullPath(path);
        }
        return null;
    }
    public static ProcessStartInfo StartInfo(string path,params string[] arguments)
    {
        ProcessStartInfo info;
        if(path.EndsWith(".cmd",StringComparison.OrdinalIgnoreCase)||path.EndsWith(".bat",StringComparison.OrdinalIgnoreCase))
        {
            var entry=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,"node_modules","@anthropic-ai","claude-code","cli.js");
            if(!File.Exists(entry))throw new IOException("Selecione claude.exe ou claude.cmd da instalação npm oficial.");
            var node=(Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)
                .Select(d=>Path.Combine(d.Trim('"'),OperatingSystem.IsWindows()?"node.exe":"node")).FirstOrDefault(File.Exists)
                ??throw new IOException("A instalação npm oficial do Claude precisa do Node.js no PATH.");
            info=new(node);info.ArgumentList.Add(entry);
        }
        else info=new(path);
        info.UseShellExecute=false;
        foreach(var argument in arguments)info.ArgumentList.Add(argument);
        return info;
    }
    internal static ProcessStartInfo Copy(ProcessStartInfo source)
    {
        if(!string.IsNullOrEmpty(source.Arguments))throw new ArgumentException("Use ArgumentList para os argumentos da instalação Claude.");
        if(source.FileName.EndsWith(".cmd",StringComparison.OrdinalIgnoreCase)||source.FileName.EndsWith(".bat",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Resolva o wrapper npm com ClaudeLocator.StartInfo antes de conectar.");
        var copy=new ProcessStartInfo(source.FileName){WorkingDirectory=source.WorkingDirectory,UseShellExecute=false};
        foreach(var argument in source.ArgumentList)copy.ArgumentList.Add(argument);
        foreach(var pair in source.Environment)copy.Environment[pair.Key]=pair.Value;
        return copy;
    }
}
