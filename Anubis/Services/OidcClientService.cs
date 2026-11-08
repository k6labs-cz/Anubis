using System.Security.Cryptography;

using k6labs.Anubis.Data.Entities;
using k6labs.Anubis.Models;

using OpenIddict.Abstractions;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace k6labs.Anubis.Services;

/// <summary>Summary of an OIDC client for the admin UI.</summary>
public sealed record ClientSummary(
    string Id,
    string ClientId,
    string? DisplayName,
    string? ClientType,
    bool RequireConsent,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> GrantTypes);

/// <summary>CRUD operations over OpenIddict applications (clients).</summary>
public sealed class OidcClientService
{
    private readonly IOpenIddictApplicationManager _applicationManager;

    public OidcClientService(IOpenIddictApplicationManager applicationManager)
    {
        _applicationManager = applicationManager;
    }

    public async Task<List<ClientSummary>> ListAsync()
    {
        var result = new List<ClientSummary>();

        await foreach (var application in _applicationManager.ListAsync())
        {
            var permissions = (await _applicationManager.GetPermissionsAsync(application!)).ToList();
            var id = await _applicationManager.GetIdAsync(application!)
                ?? throw new InvalidOperationException("An OpenIddict application has no identifier.");
            var clientId = await _applicationManager.GetClientIdAsync(application!)
                ?? throw new InvalidOperationException($"OpenIddict application '{id}' has no client identifier.");

            result.Add(new ClientSummary(
                id,
                clientId,
                await _applicationManager.GetDisplayNameAsync(application!),
                await _applicationManager.GetClientTypeAsync(application!),
                application is Client client && client.RequireConsent,
                (await _applicationManager.GetRedirectUrisAsync(application!)).Select(uri => uri.ToString()).ToList(),
                permissions
                    .Where(permission => permission.StartsWith(Permissions.Prefixes.Scope, StringComparison.Ordinal))
                    .Select(permission => permission[Permissions.Prefixes.Scope.Length..])
                    .ToList(),
                permissions
                    .Where(permission => permission.StartsWith(Permissions.Prefixes.GrantType, StringComparison.Ordinal))
                    .Select(permission => permission[Permissions.Prefixes.GrantType.Length..])
                    .ToList()));
        }

        return result;
    }

    public async Task<Client?> FindByClientIdAsync(string clientId)
        => await _applicationManager.FindByClientIdAsync(clientId) as Client;

    public async Task CreateAsync(ClientFormModel model)
    {
        var descriptor = OidcClientDescriptorFactory.Create(model);
        var application = await _applicationManager.CreateAsync(descriptor);

        if (application is Client client)
        {
            client.RequireConsent = model.RequireConsent;
            await _applicationManager.UpdateAsync(client);
        }
    }

    public async Task UpdateAsync(string clientId, ClientFormModel model)
    {
        var application = await _applicationManager.FindByClientIdAsync(clientId)
            ?? throw new InvalidOperationException(Translations.Error_ClientNotFound.F(clientId));

        var descriptor = OidcClientDescriptorFactory.Create(model);
        descriptor.ClientId = clientId; // the client id is immutable
        await _applicationManager.UpdateAsync(application, descriptor);

        if (application is Client client)
        {
            client.RequireConsent = model.RequireConsent;
            await _applicationManager.UpdateAsync(client);
        }

        if (!string.IsNullOrWhiteSpace(model.ClientSecret))
        {
            await _applicationManager.UpdateAsync(application, model.ClientSecret);
        }
    }

    public async Task DeleteAsync(string clientId)
    {
        var application = await _applicationManager.FindByClientIdAsync(clientId)
            ?? throw new InvalidOperationException(Translations.Error_ClientNotFound.F(clientId));

        await _applicationManager.DeleteAsync(application);
    }

    /// <summary>Generates a new secret for a confidential client and returns it in plain text (shown once).</summary>
    public async Task<string> RegenerateSecretAsync(string clientId)
    {
        var application = await _applicationManager.FindByClientIdAsync(clientId)
            ?? throw new InvalidOperationException(Translations.Error_ClientNotFound.F(clientId));

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await _applicationManager.UpdateAsync(application, secret);
        return secret;
    }

    public static string GenerateSecret()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
