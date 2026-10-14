using System.IO;
using NightShift.Core;

namespace NightShift.App;

public static class PortableDataMigration
{
    public static void TryImportPreviousVersion(AppStore target)
    {
        var executableDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (!File.Exists(Path.Combine(executableDirectory, "nightshift.portable"))) return;
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(executableDirectory))?.FullName;
        if (parent is null) return;
        try { ImportMissing(target.Root, Path.Combine(parent, "Night-Shift-v0.5-Windows-x64", "Dados")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void ImportMissing(string targetRoot, string previousRoot)
    {
        if (!Directory.Exists(previousRoot) ||
            string.Equals(Path.GetFullPath(targetRoot), Path.GetFullPath(previousRoot), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(targetRoot);
        CopyIfMissing(Path.Combine(previousRoot, "settings.json"), Path.Combine(targetRoot, "settings.json"));
        var sourceSessions = Path.Combine(previousRoot, "sessions");
        if (!Directory.Exists(sourceSessions)) return;
        var targetSessions = Path.Combine(targetRoot, "sessions");
        Directory.CreateDirectory(targetSessions);
        foreach (var source in Directory.EnumerateFiles(sourceSessions, "*.json"))
            CopyIfMissing(source, Path.Combine(targetSessions, Path.GetFileName(source)));
    }

    private static void CopyIfMissing(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination)) return;
        var temporary = destination + ".import-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}
