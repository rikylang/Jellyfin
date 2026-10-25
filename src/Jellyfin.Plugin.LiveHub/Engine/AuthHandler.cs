using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Http;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// Applies the authentication of a definition to outgoing requests (API key, bearer, basic, cookie, OAuth2 client credentials).
/// </summary>
public static class AuthHandler
{
    private static readonly SemaphoreSlim TokenLock = new(1, 1);

    /// <summary>Applies authentication to a request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="ctx">The run context.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task ApplyAsync(FetchRequest request, ProviderRunContext ctx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ctx);
        var auth = ctx.Definition.Connection?.Auth;
        if (auth is null || auth.Type == AuthType.None)
        {
            return;
        }

        var t = ctx.Template;
        switch (auth.Type)
        {
            case AuthType.ApiKey:
                var name = string.IsNullOrWhiteSpace(auth.Name) ? "X-Api-Key" : auth.Name;
                var value = t.Render(auth.Value);
                if (auth.In == KeyLocation.Query)
                {
                    request.Url = AppendQuery(request.Url, name, value);
                }
                else
                {
                    request.Headers[name] = value;
                }

                break;
            case AuthType.Bearer:
                request.Headers["Authorization"] = "Bearer " + t.Render(auth.Value);
                break;
            case AuthType.Basic:
                var raw = t.Render(auth.Username) + ":" + t.Render(auth.Password);
                request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                break;
            case AuthType.Cookie:
                var cookieName = string.IsNullOrWhiteSpace(auth.Name) ? null : auth.Name;
                var cookieValue = t.Render(auth.Value);
                request.Headers["Cookie"] = cookieName is null ? cookieValue : cookieName + "=" + cookieValue;
                break;
            case AuthType.Oauth2ClientCredentials:
                var token = await GetOAuthTokenAsync(ctx, auth, cancellationToken).ConfigureAwait(false);
                request.Headers["Authorization"] = "Bearer " + token;
                break;
        }
    }

    /// <summary>Forgets the cached OAuth2 token (after a 401).</summary>
    /// <param name="ctx">The run context.</param>
    public static void InvalidateToken(ProviderRunContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Gateway.OAuthToken = null;
        ctx.Gateway.OAuthExpires = DateTimeOffset.MinValue;
    }

    /// <summary>Appends a query parameter to a URL.</summary>
    /// <param name="url">The URL.</param>
    /// <param name="name">The name.</param>
    /// <param name="value">The value.</param>
    /// <returns>The new URL.</returns>
    public static Uri AppendQuery(Uri url, string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(url);
        var builder = new UriBuilder(url);
        var pair = Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value ?? string.Empty);
        builder.Query = string.IsNullOrEmpty(builder.Query) || builder.Query == "?" ? pair : builder.Query.TrimStart('?') + "&" + pair;
        return builder.Uri;
    }

    private static async Task<string> GetOAuthTokenAsync(ProviderRunContext ctx, AuthSpec auth, CancellationToken cancellationToken)
    {
        var gw = ctx.Gateway;
        if (gw.OAuthToken is not null && gw.OAuthExpires > ctx.Clock.GetUtcNow())
        {
            return gw.OAuthToken;
        }

        await TokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (gw.OAuthToken is not null && gw.OAuthExpires > ctx.Clock.GetUtcNow())
            {
                return gw.OAuthToken;
            }

            var t = ctx.Template;
            var tokenUrl = t.Render(auth.TokenUrl);
            if (!Uri.TryCreate(tokenUrl, UriKind.Absolute, out var uri))
            {
                throw new ProviderRequestException("OAuth2 tokenUrl is not a valid absolute URL.");
            }

            var form = new List<string>
            {
                "grant_type=client_credentials",
                "client_id=" + Uri.EscapeDataString(t.Render(auth.ClientId)),
                "client_secret=" + Uri.EscapeDataString(t.Render(auth.ClientSecret)),
            };
            if (!string.IsNullOrWhiteSpace(auth.Scope))
            {
                form.Add("scope=" + Uri.EscapeDataString(auth.Scope));
            }

            var request = ctx.NewRequest(uri);
            request.Method = HttpMethod.Post;
            request.Body = string.Join('&', form);
            request.ContentType = "application/x-www-form-urlencoded";
            request.Headers["Accept"] = "application/json";
            var response = await gw.Http.FetchAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                throw new ProviderRequestException($"OAuth2 token request failed with HTTP {response.StatusCode}.") { StatusCode = response.StatusCode };
            }

            var json = JsonNode.Parse(response.Text());
            var token = JsonPath.ToText(json?["access_token"]);
            if (string.IsNullOrEmpty(token))
            {
                throw new ProviderRequestException("OAuth2 response has no access_token.");
            }

            var expiresIn = double.TryParse(JsonPath.ToText(json?["expires_in"]), NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ? e : 3600;
            gw.OAuthToken = token;
            gw.OAuthExpires = ctx.Clock.GetUtcNow().AddSeconds(Math.Max(30, expiresIn - 60));
            return token;
        }
        finally
        {
            TokenLock.Release();
        }
    }
}
