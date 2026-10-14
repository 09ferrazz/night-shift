using System.Collections.ObjectModel;
using System.IO;
using NightShift.Core;

namespace NightShift.App;

public sealed partial class MainViewModel
{
    public ObservableCollection<Attachment> Attachments { get; } = [];
    private bool _addingAttachments;
    private int _attachmentContext;
    private readonly Dictionary<string, int> _attachmentPins = [];
    public Func<string, Task>? ValidateImageAsync { get; set; }
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasDraft => !string.IsNullOrWhiteSpace(Draft) || HasAttachments;
    public bool CanAttach => CanCompose && !_stopping && !_addingAttachments;
    public async Task AddAttachmentsAsync(IEnumerable<string> paths)
    {
        if (!CanAttach) return;
        var selected = paths.ToArray();
        if (selected.Length + Attachments.Count > AttachmentStore.MaxAttachments)
            throw new InvalidOperationException("Envie até 10 anexos por mensagem. Cada arquivo pode ter até 25 MB.");
        var context = _attachmentContext;
        _addingAttachments = true; NotifyAll();
        var store = new AttachmentStore(_store.Root);
        var imported = new List<Attachment>();
        var committed = false;
        try
        {
            foreach (var path in selected)
            {
                var item = await store.ImportAsync(path);
                imported.Add(item);
                if (item.IsImage && ValidateImageAsync is not null) await ValidateImageAsync(item.Path);
            }
            if (context == _attachmentContext && !_disposed)
            {
                foreach (var item in imported) Attachments.Add(item);
                committed = true;
            }
        }
        finally
        {
            if (!committed) foreach (var item in imported) store.Discard(item);
            _addingAttachments = false; NotifyAll();
        }
    }
    public void RemoveAttachment(Attachment item)
    {
        if (Attachments.Remove(item) && !IsPinned(item)) DiscardUnused(item);
        NotifyAll();
    }
    private void ClearAttachmentDraft()
    {
        _attachmentContext++;
        var unused = Attachments.ToArray();
        Attachments.Clear();
        foreach (var item in unused) if (!IsPinned(item)) DiscardUnused(item);
        NotifyAll();
    }
    private void PinAttachments(IReadOnlyList<Attachment> items)
    {
        lock (_attachmentPins)
            foreach (var item in items)
                _attachmentPins[item.Id] = _attachmentPins.GetValueOrDefault(item.Id) + 1;
    }
    private bool IsPinned(Attachment item)
    {
        lock (_attachmentPins) return _attachmentPins.ContainsKey(item.Id);
    }
    private void ReleaseAttachments(IReadOnlyList<Attachment> items, bool accepted)
    {
        foreach (var item in items)
        {
            bool stillPinned;
            lock (_attachmentPins)
            {
                var count = _attachmentPins[item.Id] - 1;
                if (count == 0) _attachmentPins.Remove(item.Id);
                else _attachmentPins[item.Id] = count;
                stillPinned = count > 0;
            }
            if (!accepted && !stillPinned && !Attachments.Contains(item)) DiscardUnused(item);
        }
    }
    private void DiscardUnused(Attachment item)
    {
        try { new AttachmentStore(_store.Root).Discard(item); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void AcceptDraft(long revision, IReadOnlyList<Attachment> sent)
    {
        if (_draftRevision == revision) Draft = "";
        foreach (var item in sent) Attachments.Remove(item);
        _dirty = true; NotifyAll();
    }
}
