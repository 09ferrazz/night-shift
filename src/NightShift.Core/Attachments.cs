using System.Text.Json;

namespace NightShift.Core;

public sealed record Attachment(string Id, string Name, string Path, bool IsImage, long Length)
{
    public string Detail => (IsImage ? "Imagem" : "Arquivo local") + " · " + (Length < 1024 * 1024 ? $"{Math.Max(1, Length / 1024)} KB" : $"{Length / (1024.0 * 1024):0.#} MB");
    public string Help => IsImage ? "Imagem enviada ao Codex. Remova com × antes de enviar." : "Cópia local para o Codex ler. O suporte ao formato depende das ferramentas e permissões da sessão.";
}

public sealed class AttachmentStore(string dataRoot)
{
    public const long MaxFileBytes = 25 * 1024 * 1024;
    public const int MaxAttachments = 10;
    public async Task<Attachment> ImportAsync(string path)
    {
        var source = new FileInfo(path);
        if (!source.Exists) throw new FileNotFoundException("Arquivo não encontrado: " + source.Name, path);
        if (source.Length > MaxFileBytes) throw new InvalidOperationException("Cada anexo pode ter até 25 MB: " + source.Name);
        var image = source.Extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp";
        await using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (image)
        {
            var header = new byte[12]; var count = await input.ReadAsync(header); input.Position = 0;
            var valid = source.Extension.ToLowerInvariant() switch
            {
                ".png" => count >= 8 && header.AsSpan(0,8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}),
                ".jpg" or ".jpeg" => count >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255,
                _ => count >= 12 && header.AsSpan(0,4).SequenceEqual("RIFF"u8) && header.AsSpan(8,4).SequenceEqual("WEBP"u8)
            };
            if (!valid) throw new InvalidOperationException("O arquivo não tem um formato de imagem válido: " + source.Name);
        }
        var id = Guid.NewGuid().ToString("N");
        var directory = System.IO.Path.Combine(dataRoot, "attachments", id);
        Directory.CreateDirectory(directory);
        var destination = System.IO.Path.Combine(directory, source.Name);
        try
        {
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; long length = 0; int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    length += read;
                    if (length > MaxFileBytes) throw new InvalidOperationException("O anexo excedeu 25 MB durante a cópia.");
                    await output.WriteAsync(buffer.AsMemory(0, read));
                }
            }
            return new(id, source.Name, destination, image, new FileInfo(destination).Length);
        }
        catch { File.Delete(destination); Directory.Delete(directory); throw; }
    }

    public void Discard(Attachment item)
    {
        if (!Guid.TryParseExact(item.Id, "N", out _)) return;
        var directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(dataRoot, "attachments", item.Id));
        var expected = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, item.Name));
        if (!string.Equals(expected, System.IO.Path.GetFullPath(item.Path), StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(expected)) File.Delete(expected);
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
}

public static class AttachmentInput
{
    private const string Marker = "\n\n[Night Shift · anexos locais v1]\n";
    private const string End = "\n[/Night Shift · anexos locais]";
    public static object[] Build(string text, IReadOnlyList<Attachment>? attachments = null)
    {
        if (attachments is null || attachments.Count == 0) return [new { type = "text", text }];
        var manifest = text + Marker + JsonSerializer.Serialize(attachments) + End;
        return new object[] { new { type = "text", text = manifest } }
            .Concat(attachments.Where(a => a.IsImage).Select(a => (object)new { type = "localImage", path = a.Path })).ToArray();
    }
    public static (string Text, List<Attachment> Attachments) Read(string text)
    {
        var start = text.LastIndexOf(Marker, StringComparison.Ordinal);
        if (start < 0 || !text.EndsWith(End, StringComparison.Ordinal)) return (text, []);
        try
        {
            var items = JsonSerializer.Deserialize<List<Attachment>>(text[(start + Marker.Length)..^End.Length]);
            if (items is null || items.Count is 0 or > AttachmentStore.MaxAttachments
                || items.Any(a => a is null || string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.Name)
                    || string.IsNullOrWhiteSpace(a.Path) || !System.IO.Path.IsPathFullyQualified(a.Path)
                    || !Guid.TryParseExact(a.Id, "N", out _) || a.Length < 0 || a.Length > AttachmentStore.MaxFileBytes
                    || !string.Equals(System.IO.Path.GetFileName(a.Path), a.Name, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(a.Path)), a.Id, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(a.Path))), "attachments", StringComparison.OrdinalIgnoreCase))) return (text, []);
            return (text[..start], items);
        }
        catch (JsonException) { return (text, []); }
    }
    public static void Validate(IReadOnlyList<Attachment>? attachments)
    {
        if (attachments is null) return;
        if (attachments.Count > AttachmentStore.MaxAttachments) throw new InvalidOperationException("Envie até 10 anexos por mensagem.");
        foreach (var item in attachments)
        {
            var file = new FileInfo(item.Path);
            if (!file.Exists || file.Length != item.Length) throw new IOException("O anexo foi removido ou alterado: " + item.Name + ". Anexe novamente.");
            if (file.Length > AttachmentStore.MaxFileBytes) throw new IOException("O anexo excede 25 MB: " + item.Name);
        }
    }
}
