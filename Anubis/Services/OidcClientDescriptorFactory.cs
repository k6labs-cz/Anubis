using k6labs.Anubis.Models;
using k6labs.Anubis.Options;

using OpenIddict.Abstractions;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace k6labs.Anubis.Services;

/// <summary>Builds <see cref="OpenIddictApplicationDescriptor"/> instances from Anubis form/config models.</summary>
public static class OidcClientDescriptorFactory
{
    public static OpenIddictApplicationDescriptor Create(ClientFormModel model)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = model.ClientId,
            DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? model.ClientId : model.DisplayName,
            ClientType = string.IsNullOrWhiteSpace(model.ClientSecret) ? ClientTypes.Public : ClientTypes.Confidential,
        };

        if (!string.IsNullOrWhiteSpace(model.ClientSecret))
        {
            descriptor.ClientSecret = model.ClientSecret;
        }

        AddUris(descriptor.RedirectUris, model.RedirectUrisText);
        AddUris(descriptor.PostLogoutRedirectUris, model.PostLogoutRedirectUrisText);
        AddPermissions(descriptor, model.AllowAuthorizationCodeFlow, model.AllowRefreshTokenFlow, model.AllowClientCredentialsFlow, model.Scopes);

        return descriptor;
    }

    public static OpenIddictApplicationDescriptor Create(SeedClientOptions client)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = client.ClientId,
            DisplayName = string.IsNullOrWhiteSpace(client.DisplayName) ? client.ClientId : client.DisplayName,
            ClientType = client.ClientType.Equals("public", StringComparison.OrdinalIgnoreCase)
                ? ClientTypes.Public
                : ClientTypes.Confidential,
        };

        if (!string.IsNullOrWhiteSpace(client.ClientSecret))
        {
            descriptor.ClientSecret = client.ClientSecret;
        }

        AddUris(descriptor.RedirectUris, client.RedirectUris);
        AddUris(descriptor.PostLogoutRedirectUris, client.PostLogoutRedirectUris);

        var grantTypes = client.GrantTypes.Select(grant => grant.ToLowerInvariant()).ToList();
        AddPermissions(
            descriptor,
            grantTypes.Contains(GrantTypes.AuthorizationCode),
            grantTypes.Contains(GrantTypes.RefreshToken),
            grantTypes.Contains(GrantTypes.ClientCredentials),
            client.Scopes);

        return descriptor;
    }

    private static void AddPermissions(
        OpenIddictApplicationDescriptor descriptor,
        bool allowAuthorizationCodeFlow,
        bool allowRefreshTokenFlow,
        bool allowClientCredentialsFlow,
        IEnumerable<string> scopes)
    {
        descriptor.Permissions.Add(Permissions.Endpoints.Token);

        if (allowAuthorizationCodeFlow)
        {
            descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
            descriptor.Permissions.Add(Permissions.ResponseTypes.Code);
            descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
            descriptor.Permissions.Add(Permissions.Endpoints.EndSession);
            descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        }

        if (allowRefreshTokenFlow)
        {
            descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
        }

        if (allowClientCredentialsFlow)
        {
            descriptor.Permissions.Add(Permissions.GrantTypes.ClientCredentials);
            // Machine-to-machine clients may introspect and revoke tokens.
            descriptor.Permissions.Add(Permissions.Endpoints.Introspection);
            descriptor.Permissions.Add(Permissions.Endpoints.Revocation);
        }

        foreach (var scope in scopes.Select(scope => scope.Trim()).Where(scope => scope.Length > 0).Distinct())
        {
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        }
    }

    private static void AddUris(ISet<Uri> target, IEnumerable<string> uris)
    {
        foreach (var uri in uris)
        {
            var trimmed = uri.Trim();
            if (trimmed.Length > 0)
            {
                target.Add(new Uri(trimmed, UriKind.Absolute));
            }
        }
    }

    private static void AddUris(ISet<Uri> target, string multiLineText)
        => AddUris(target, multiLineText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
