using System.Reflection;
using Hermex.Internal;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Hermex.Dashboard;

/// <summary>
/// Applies <see cref="HermexAuthorizationOptions"/> to Hermex endpoints: each one is tagged with
/// <see cref="HermexEndpointMetadata"/> and, when its tier has a policy, requires that policy.
/// </summary>
internal static class HermexEndpointAuthorization
{
    /// <summary>Tags an endpoint (or group) with its tier and requires the tier's effective policy.</summary>
    public static TBuilder WithHermexTier<TBuilder>(this TBuilder builder, HermexEndpointTier tier,
        HermexAuthorizationOptions authorization)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new HermexEndpointMetadata(tier));
        var policy = authorization.GetEffectivePolicy(tier);
        if (policy is not null)
            builder.RequireAuthorization(policy);
        return builder;
    }

    /// <summary>
    /// Whether the caller passes <paramref name="tier"/>'s effective policy. Used where an endpoint
    /// of a lower tier shows extra detail to higher tiers (status, the hub's live logs). Evaluated
    /// like the authorization middleware, so a policy that names its own authentication schemes
    /// authenticates with them rather than with the endpoint's principal. Fails closed.
    /// </summary>
    public static async Task<bool> IsAuthorizedForTierAsync(HttpContext? context,
        HermexAuthorizationOptions authorization, HermexEndpointTier tier)
    {
        var name = authorization.GetEffectivePolicy(tier);
        if (name is null)
            return true;
        if (context is null)
            return false;

        var services = context.RequestServices;
        var policy = await services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(name)
            .ConfigureAwait(false);
        if (policy is null)
            return false;

        // AuthenticateAsync replaces context.User when the policy names schemes; this is only a
        // probe, so give the rest of the pipeline its original caller back.
        var caller = context.User;
        try
        {
            var evaluator = services.GetRequiredService<IPolicyEvaluator>();
            var authentication = await evaluator.AuthenticateAsync(policy, context).ConfigureAwait(false);
            // The middleware passes the HttpContext as the resource; do the same so resource-aware handlers agree.
            var result = await evaluator.AuthorizeAsync(policy, authentication, context, resource: context)
                .ConfigureAwait(false);
            return result.Succeeded;
        }
        finally
        {
            context.User = caller;
        }
    }

    /// <summary>
    /// Refuses hub requests from other sites. Browsers apply no CORS to WebSockets and SignalR can
    /// connect without negotiating, so a page on another site could otherwise open the hub with the
    /// user's ambient credentials (Windows sign-in, <c>SameSite=None</c> cookies) and read live mail
    /// summaries and logs. Wraps the endpoint itself, so it does not depend on middleware order.
    /// </summary>
    public static TBuilder RequireSameOrigin<TBuilder>(this TBuilder builder, HermexAuthorizationOptions authorization)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint =>
        {
            var next = endpoint.RequestDelegate;
            if (next is null)
                return;
            endpoint.RequestDelegate = context =>
            {
                if (IsAllowedOrigin(context.Request, authorization))
                    return next(context);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        return builder;
    }

    /// <summary>
    /// No <c>Origin</c> (non-browser client), the request's own host and port (scheme ignored, since
    /// TLS-terminating proxies change it), or an entry in <see cref="HermexAuthorizationOptions.AllowedHubOrigins"/>.
    /// <c>Origin: null</c> (sandboxed documents, such as the message preview) is refused.
    /// </summary>
    internal static bool IsAllowedOrigin(HttpRequest request, HermexAuthorizationOptions authorization)
    {
        if (!request.Headers.TryGetValue(HeaderNames.Origin, out var values))
            return true;

        var origin = values.ToString().Trim();
        if (authorization.AllowedHubOrigins.Any(allowed =>
                string.Equals(allowed?.Trim().TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return false;
        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return string.Equals(authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Endpoint filter that refuses state-changing API calls without the
    /// <see cref="HermexRoutes.RequestHeader"/> header. A custom header makes the browser send a
    /// CORS preflight, so another site cannot trigger these calls with the user's cookie.
    /// </summary>
    public static ValueTask<object?> RequireRequestHeader(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) ||
            request.Headers.ContainsKey(HermexRoutes.RequestHeader))
        {
            return next(context);
        }

        return ValueTask.FromResult<object?>(Results.BadRequest(
            new { error = $"The {HermexRoutes.RequestHeader} header is required." }));
    }

    /// <summary>
    /// Fails fast at startup instead of serving a dashboard whose policies cannot be evaluated:
    /// every policy name must be registered, and each policy needs an authentication scheme to
    /// challenge with (its own, or the host's default). <c>UseHermex</c> is synchronous, so this
    /// blocks on the async providers once at startup, as the store initialisation there does.
    /// </summary>
    public static void Verify(IServiceProvider services, HermexAuthorizationOptions authorization)
    {
        if (!authorization.IsEnabled)
            return;

        // Always present: AddHermex's AddRazorPages registers the authorization services.
        var policies = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var defaultChallenge = services.GetService<IAuthenticationSchemeProvider>()?
            .GetDefaultChallengeSchemeAsync().GetAwaiter().GetResult();

        foreach (var tier in Enum.GetValues<HermexEndpointTier>())
        {
            var name = authorization.GetEffectivePolicy(tier);
            if (name is null)
                continue;

            var policy = policies.GetPolicyAsync(name).GetAwaiter().GetResult()
                ?? throw new HermexConfigurationException(
                    $"HermexOptions.Authorization: the {tier} policy '{name}' is not registered. " +
                    "Add it with builder.Services.AddAuthorization(o => o.AddPolicy(...)).");

            if (policy.AuthenticationSchemes.Count == 0 && defaultChallenge is null)
            {
                throw new HermexConfigurationException(
                    $"HermexOptions.Authorization: the {tier} policy '{name}' has no authentication scheme " +
                    "and the host has no default one. Call builder.Services.AddAuthentication(...) with a " +
                    "default scheme, or name schemes on the policy.");
            }
        }
    }
}

/// <summary>
/// Adds tier metadata and authorization to Hermex's own Razor Pages. Hermex cannot rely on
/// <c>MapRazorPages()</c> conventions because the host may map the pages itself, so this runs
/// on the page models instead, and only on pages compiled into the Hermex assembly, so the
/// host's pages are never touched.
/// </summary>
internal sealed class HermexPageConvention : IPageApplicationModelConvention
{
    private static readonly Assembly HermexAssembly = typeof(HermexPageConvention).Assembly;

    private readonly HermexAuthorizationOptions _authorization;

    public HermexPageConvention(HermexAuthorizationOptions authorization) => _authorization = authorization;

    public void Apply(PageApplicationModel model)
    {
        if (model.PageType.Assembly != HermexAssembly)
            return;

        // Only the inbox is a Viewer page; anything else, including pages added later, is Admin.
        var tier = string.Equals(model.ViewEnginePath, "/Inbox", StringComparison.OrdinalIgnoreCase)
            ? HermexEndpointTier.Viewer
            : HermexEndpointTier.Admin;

        model.EndpointMetadata.Add(new HermexEndpointMetadata(tier));
        var policy = _authorization.GetEffectivePolicy(tier);
        if (policy is not null)
            model.EndpointMetadata.Add(new AuthorizeAttribute(policy));
    }
}
