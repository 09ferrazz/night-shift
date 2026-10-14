using System.Text.Json;
using System.Text;
namespace NightShift.Core;

/// <summary>Read-only views of official agents output and local Claude transcript files.</summary>
public static class ClaudeCatalog
{
    public static bool IsLive(SessionRecord record) => record.RuntimeStatus is "busy" or "waiting" or "idle" or "working" or "blocked";
    public static IReadOnlyList<SessionRecord> ReadLive(JsonElement data,string? project=null)
    {
        if(data.ValueKind!=JsonValueKind.Array)throw new IOException("Claude agents não retornou uma lista JSON.");
        var records=new List<SessionRecord>();
        foreach(var row in data.EnumerateArray().Take(500))
        {
            var kind=CodexSession.Text(row,"kind");
            // Native interactive agents have a sessionId, but only background agents require a short attach id.
            var id=CodexSession.Text(row,"id")??(kind=="interactive"?CodexSession.Text(row,"sessionId"):null);var cwd=CodexSession.Text(row,"cwd");
            if(string.IsNullOrWhiteSpace(id)||string.IsNullOrWhiteSpace(cwd)||!Path.IsPathFullyQualified(cwd)||kind is not ("interactive" or "background"))continue;
            if(project is not null&&!SessionCatalog.SameProject(cwd,project))continue;
            var updated=DateTimeOffset.MinValue;
            if(row.TryGetProperty("startedAt",out var at)&&at.TryGetInt64(out var millis))
                try{updated=DateTimeOffset.FromUnixTimeMilliseconds(millis);}catch(ArgumentOutOfRangeException){}
            var status=CodexSession.Text(row,"status")??CodexSession.Text(row,"state")??"Não informado";
            records.Add(new(){Provider=AgentProvider.Claude,ThreadId=CodexSession.Text(row,"sessionId")??id,ExternalId=id,ExternalKind=kind,
                ProjectPath=cwd,Title=CodexSession.Text(row,"name")??"Claude · "+id,Source="claude",Originator=CodexSession.Text(row,"waitingFor")??"",RuntimeStatus=status,UpdatedAt=updated});
        }
        return records.DistinctBy(r=>r.ExternalId).ToArray();
    }
    public static string DefaultRoot => Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude"),"projects");
    private static IEnumerable<string> TranscriptFiles(string root)
    {
        if(!Directory.Exists(root))yield break;
        foreach(var directory in Directory.EnumerateDirectories(root).Take(500))
        {
            if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)continue;
            foreach(var file in Directory.EnumerateFiles(directory,"*.jsonl").Take(1000))
                if((File.GetAttributes(file)&FileAttributes.ReparsePoint)==0)yield return file;
        }
    }
    public static IReadOnlyList<SessionRecord> ReadHistory(string? root=null)
    {
        // Catalog enumeration reads short previews only. Full messages load for the selected ID.
        var files=TranscriptFiles(root??DefaultRoot).OrderByDescending(File.GetLastWriteTimeUtc).Take(100);
        return files.Select(file=>ReadTranscript(file,false)).OfType<SessionRecord>().ToArray();
    }
    public static SessionRecord? ReadRecord(string id,string? root=null)
    {
        if(!Guid.TryParse(id,out _))return null;
        var file=TranscriptFiles(root??DefaultRoot).FirstOrDefault(file=>string.Equals(Path.GetFileNameWithoutExtension(file),id,StringComparison.OrdinalIgnoreCase));
        return file is null?null:ReadTranscript(file,true);
    }
    private static SessionRecord? ReadTranscript(string file,bool includeMessages)
    {
        try
        {
            var record=new SessionRecord {Provider=AgentProvider.Claude,ThreadId=Path.GetFileNameWithoutExtension(file),Source="claude",Title="Conversa Claude",UpdatedAt=File.GetLastWriteTimeUtc(file)};
            // An unusually large native transcript remains listed, but must be opened in its original terminal.
            if(new FileInfo(file).Length>64*1024*1024&&includeMessages)throw new IOException("Transcript Claude excede 64 MB. Abra esta conversa no terminal original.");
            var scanned=0;
            foreach(var line in ReadBoundedLines(file,includeMessages))
            {
                if(!includeMessages&&++scanned>200)break;
                if(line is null)continue;
                JsonElement row;try{using var doc=JsonDocument.Parse(line);row=doc.RootElement.Clone();}catch(JsonException){continue;}
                if(row.ValueKind!=JsonValueKind.Object)continue;
                if(CodexSession.Text(row,"sessionId") is {} sessionId&&sessionId!=record.ThreadId)continue;
                if(CodexSession.Text(row,"cwd") is {} cwd&&Path.IsPathFullyQualified(cwd))record.ProjectPath=cwd;
                var role=CodexSession.Text(row,"type");
                if(role is not ("user" or "assistant")||!row.TryGetProperty("message",out var message))continue;
                var text=ReadText(message);if(string.IsNullOrWhiteSpace(text))continue;
                var parsed=AttachmentInput.Read(text);
                var display=parsed.Text;
                var preamble=Protocol.Preamble+"\n\nTarefa do usuário:\n";
                if(role=="user"&&display.StartsWith(preamble,StringComparison.Ordinal))display=display[preamble.Length..];
                if(includeMessages)record.Messages.Add(new(){Id=CodexSession.Text(row,"uuid")??Guid.NewGuid().ToString("N"),Role=role=="user"?"Você":"Claude",Text=display,Attachments=parsed.Attachments});
                if(role=="user"&&record.Title=="Conversa Claude")record.Title=display.Length>120?display[..120]+"…":display;
                if(!includeMessages&&record.ProjectPath.Length>0&&record.Title!="Conversa Claude")break;
            }
            return record.ProjectPath.Length>0?record:null;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            if(includeMessages)throw;
            return null;
        }
    }
    private static IEnumerable<string?> ReadBoundedLines(string file,bool includeMessages)
    {
        // Listing keeps only a small preview; selected history must fail explicitly rather than lose a turn.
        var limit=includeMessages?8*1024*1024:64*1024;
        using var input=new StreamReader(file);var buffer=new char[4096];var line=new StringBuilder();bool oversized=false;int read;long scanned=0;
        while((read=input.Read(buffer,0,buffer.Length))>0)
        {
            for(int i=0;i<read;i++)
            {
                if(!includeMessages&&++scanned>1024*1024)yield break;
                var c=buffer[i];
                if(c=='\n'){yield return oversized?null:line.ToString();line.Clear();oversized=false;continue;}
                if(oversized)continue;
                if(line.Length>=limit)
                {
                    if(includeMessages)throw new IOException("Evento do transcript Claude excede 8 MB. Abra esta conversa no terminal original.");
                    oversized=true;line.Clear();continue;
                }
                line.Append(c);
            }
        }
        if(oversized||line.Length>0)yield return oversized?null:line.ToString();
    }
    internal static string ReadText(JsonElement message)
    {
        if(message.ValueKind!=JsonValueKind.Object||!message.TryGetProperty("content",out var content))return "";
        if(content.ValueKind==JsonValueKind.String)return content.GetString()??"";
        return content.ValueKind==JsonValueKind.Array?string.Join("",content.EnumerateArray().Where(b=>CodexSession.Text(b,"type")=="text").Select(b=>CodexSession.Text(b,"text"))):"";
    }
}
