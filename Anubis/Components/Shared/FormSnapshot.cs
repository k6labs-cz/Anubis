using System.Text.Json;

namespace k6labs.Anubis.Components.Shared;

/// <summary>
/// Remembers a form model's state (as JSON) so a page can tell whether anything changed:
/// <c>snapshot.Take(model)</c> after loading / saving, <c>snapshot.IsDirty(model)</c> to enable "Save".
/// </summary>
public sealed class FormSnapshot
{
    private string? _saved;

    public void Take(object? model) => _saved = Serialize(model);

    public bool IsDirty(object? model) => _saved is null || _saved != Serialize(model);

    private static string Serialize(object? model) => JsonSerializer.Serialize(model, model?.GetType() ?? typeof(object));
}
