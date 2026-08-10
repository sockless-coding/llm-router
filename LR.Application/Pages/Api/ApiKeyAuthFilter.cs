using Microsoft.Extensions.Options;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Api;

/// <summary>
/// Endpoint filter gating the protocol API route groups (OpenAI, Responses, Claude, Ollama)
/// behind API-key authentication when <see cref="GatewaySettings.RequireApiKey"/> is enabled.
/// Accepts the key via either "Authorization: Bearer &lt;key&gt;" or "x-api-key: &lt;key&gt;" —
/// both conventions are accepted on every protocol surface rather than special-cased per protocol,
/// since real-world clients for any of these APIs send one or the other.
///
/// Registered once per endpoint (via <c>AddEndpointFilter&lt;ApiKeyAuthFilter&gt;()</c>), so only
/// constructor-safe (singleton) dependencies are injected here — scoped services are pulled from
/// the request's own service provider inside <see cref="InvokeAsync"/>.
/// </summary>
public class ApiKeyAuthFilter : IEndpointFilter
{
    private readonly IOptionsMonitor<GatewaySettings> _settings;

    public ApiKeyAuthFilter(IOptionsMonitor<GatewaySettings> settings)
    {
        _settings = settings;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!_settings.CurrentValue.RequireApiKey)
            return await next(context);

        var httpContext = context.HttpContext;
        var rawKey = ExtractRawKey(httpContext.Request);

        var apiKeyManager = httpContext.RequestServices.GetRequiredService<IApiKeyManager>();
        var apiKey = string.IsNullOrEmpty(rawKey) ? null : await apiKeyManager.ValidateAsync(rawKey);

        if (apiKey is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Results.Json(new
            {
                type = "error",
                error = new
                {
                    type = "authentication_error",
                    message = "Invalid or missing API key. Provide it via the Authorization: Bearer header or x-api-key header."
                }
            }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var apiKeyContext = httpContext.RequestServices.GetRequiredService<IApiKeyRequestContext>();
        apiKeyContext.CurrentKey = apiKey;

        return await next(context);
    }

    private static string? ExtractRawKey(HttpRequest request)
    {
        var authHeader = request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authHeader["Bearer ".Length..].Trim();

        var xApiKey = request.Headers["x-api-key"].ToString();
        if (!string.IsNullOrEmpty(xApiKey))
            return xApiKey.Trim();

        return null;
    }
}
