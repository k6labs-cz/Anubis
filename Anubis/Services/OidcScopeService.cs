using k6labs.Anubis.Models;

using OpenIddict.Abstractions;

namespace k6labs.Anubis.Services;

/// <summary>Summary of an OIDC scope for the admin UI.</summary>
public sealed record ScopeSummary(string Id, string Name, string? DisplayName, string? Description, IReadOnlyList<string> Resources);

/// <summary>CRUD operations over OpenIddict scopes.</summary>
public sealed class OidcScopeService
{
    private readonly IOpenIddictScopeManager _scopeManager;

    public OidcScopeService(IOpenIddictScopeManager scopeManager)
    {
        _scopeManager = scopeManager;
    }

    public async Task<List<ScopeSummary>> ListAsync()
    {
        var result = new List<ScopeSummary>();

        await foreach (var scope in _scopeManager.ListAsync())
        {
            var id = await _scopeManager.GetIdAsync(scope!)
                ?? throw new InvalidOperationException("An OpenIddict scope has no identifier.");
            var name = await _scopeManager.GetNameAsync(scope!)
                ?? throw new InvalidOperationException($"OpenIddict scope '{id}' has no name.");

            result.Add(new ScopeSummary(
                id,
                name,
                await _scopeManager.GetDisplayNameAsync(scope!),
                await _scopeManager.GetDescriptionAsync(scope!),
                (await _scopeManager.GetResourcesAsync(scope!)).ToList()));
        }

        return result;
    }

    public async Task<List<string>> ListNamesAsync()
        => (await ListAsync()).Select(scope => scope.Name).ToList();

    public async Task CreateAsync(ScopeFormModel model)
    {
        if (await _scopeManager.FindByNameAsync(model.Name) is not null)
        {
            throw new InvalidOperationException(Translations.Error_ScopeExists.F(model.Name));
        }

        var descriptor = new OpenIddictScopeDescriptor
        {
            Name = model.Name,
            DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? model.Name : model.DisplayName,
            Description = model.Description,
        };

        foreach (var resource in model.ResourcesText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            descriptor.Resources.Add(resource);
        }

        await _scopeManager.CreateAsync(descriptor);
    }

    public async Task DeleteAsync(string name)
    {
        var scope = await _scopeManager.FindByNameAsync(name)
            ?? throw new InvalidOperationException(Translations.Error_ScopeNotFound.F(name));

        await _scopeManager.DeleteAsync(scope);
    }
}
