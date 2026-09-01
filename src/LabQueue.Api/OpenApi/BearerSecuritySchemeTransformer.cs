using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LabQueue.Api.OpenApi;

/// <summary>
/// Puts the JWT bearer scheme into the document and marks the operations that actually
/// require it, so the docs page's Authorize control works rather than merely existing.
///
/// Both halves are done here, in one document transformer, and the second half is the
/// reason. The obvious shape is an IOpenApiOperationTransformer that reads endpoint
/// metadata, which is what the ASP.NET Core docs show — but on .NET 10 an
/// OpenApiSecuritySchemeReference constructed inside an operation transformer does not
/// resolve, and serialises as "security": [{}]. That is dotnet/aspnetcore#64524, closed as
/// a duplicate of microsoft/OpenAPI.NET#2300 and marked by design. The failure is silent:
/// the page renders, the button appears, and no request ever carries the header. A document
/// transformer holds the real OpenApiDocument, so references built here resolve.
/// </summary>
internal sealed class BearerSecuritySchemeTransformer(IAuthenticationSchemeProvider schemeProvider)
    : IOpenApiDocumentTransformer
{
    private const string SchemeName = JwtBearerDefaults.AuthenticationScheme;

    /// <summary>Strips constraints, optionality and defaults: {id:guid} and {id?} both become {id}.</summary>
    private static readonly Regex RouteConstraint = new(@"\{([^:?=}]+)[^}]*\}", RegexOptions.Compiled);

    public async Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "labqueue",
            Version = "v1",
            Description =
                "Lab equipment reservation API — book instruments for a window of time, with "
                + "certification gating and maintenance windows.\n\n"
                + "Booking enforces five rules in order: the resource exists and is active → the "
                + "window is well formed → the caller holds the required unexpired certification "
                + "→ no maintenance overlap → no confirmed-reservation overlap. The last is "
                + "enforced by the database, by a partial GiST exclusion constraint, and is what "
                + "makes a second booking of the same window return 409.\n\n"
                + "Source: https://github.com/LuisZarate17/labqueue"
        };

        // Only advertise auth the app actually has. Absent the scheme this leaves the document
        // describing an API anyone can call, which would at least be true.
        var schemes = await schemeProvider.GetAllSchemesAsync();
        if (!schemes.Any(scheme => scheme.Name == SchemeName))
        {
            return;
        }

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            In = ParameterLocation.Header,
            BearerFormat = "JWT",
            Description =
                "Paste the token from POST /auth/login or POST /auth/register. Bearer prefix not needed."
        };

        foreach (var description in context.DescriptionGroups.SelectMany(group => group.Items))
        {
            // The interfaces, not AuthorizeAttribute/AllowAnonymousAttribute. Every route here
            // is configured fluently — .RequireAuthorization() / .AllowAnonymous() on the group —
            // and those add metadata implementing these, not the attributes themselves.
            var metadata = description.ActionDescriptor.EndpointMetadata;
            var requiresAuth = metadata.OfType<IAuthorizeData>().Any()
                               && !metadata.OfType<IAllowAnonymous>().Any();

            if (!requiresAuth || description.RelativePath is null || description.HttpMethod is null)
            {
                continue;
            }

            // ApiDescription.RelativePath keeps route constraints — "resources/{id:guid}" —
            // while the document keys the path without them, "/resources/{id}". Matching the
            // two raw silently skips every parameterised route, which is half the protected
            // surface here, and leaves a docs page where those operations look anonymous.
            //
            // Operations are keyed by System.Net.Http.HttpMethod in OpenAPI.NET v2, not by the
            // OperationType enum v1 used.
            var path = "/" + RouteConstraint.Replace(description.RelativePath, "{$1}").TrimEnd('/');
            if (!document.Paths.TryGetValue(path, out var pathItem)
                || pathItem.Operations is null
                || !pathItem.Operations.TryGetValue(HttpMethod.Parse(description.HttpMethod), out var operation))
            {
                continue;
            }

            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SchemeName, document)] = []
            });
        }
    }
}
