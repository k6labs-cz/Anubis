using k6labs.Hathor.Feedback;

namespace k6labs.Anubis.Components.Shared;

/// <summary>Notifications owned by one page, including messages queued before its first render.</summary>
public sealed class NotificationState
{
    private readonly List<Notice> items = [];
    public IReadOnlyList<Notice> Items => items;
    public event Action? Changed;

    public void Add(string text, AlertSeverity kind)
    {
        items.Add(new Notice(text, kind));
        if (items.Count > 5) items.RemoveAt(0);
        Changed?.Invoke();
    }

    public void Dismiss(Notice item)
    {
        items.Remove(item);
        Changed?.Invoke();
    }

    public sealed record Notice(string Text, AlertSeverity Kind);
}
