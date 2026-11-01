using Microsoft.Extensions.Options;
using VakifAtlas.Identity.Api.Infrastructure;
using VakifAtlas.ServiceDefaults.Auth;

namespace VakifAtlas.Identity.Api.Features.Discovery;

/// <summary>
/// OIDC keşfi + JWKS: diğer servisler erişim anahtarını bu açık anahtarlarla doğrular.
/// Yalnızca küme içinde yayınlanır (gateway <c>.well-known</c>'ı yönlendirmez).
/// </summary>
public static class DiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration", (IOptions<AuthOptions> auth) => TypedResults.Ok(new Dictionary<string, object>
        {
            ["issuer"] = auth.Value.Issuer,
            ["jwks_uri"] = $"{auth.Value.Authority.TrimEnd('/')}/.well-known/jwks.json",
            ["id_token_signing_alg_values_supported"] = new[] { "ES256" },
        })).AllowAnonymous().ExcludeFromDescription();

        app.MapGet("/.well-known/jwks.json", (SigningKeyStore keys) => TypedResults.Ok(new
        {
            keys = keys.PublicJwks().Select(k => new { kty = k.Kty, use = k.Use, alg = k.Alg, kid = k.Kid, crv = k.Crv, x = k.X, y = k.Y }),
        })).AllowAnonymous().ExcludeFromDescription();

        return app;
    }
}
