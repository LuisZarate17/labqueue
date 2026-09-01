using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LabQueue.Tests.Infrastructure;

namespace LabQueue.Tests;

/// <summary>
/// The docs are only worth anything on the deployed instance, and every way they fail there
/// is silent — they keep working locally. These four guard the four ways.
///
/// The fixture runs under ASPNETCORE_ENVIRONMENT=Testing, which is what makes the first test
/// a real check rather than a tautology: wrapping MapOpenApi in IsDevelopment(), the default
/// the templates ship, would fail here exactly as it would fail in Production.
/// </summary>
public class DocumentationTests(LabQueueApiFixture fixture) : IClassFixture<LabQueueApiFixture>
{
    private const string DocumentPath = "/openapi/v1.json";

    /// <summary>
    /// Every operation the API exposes, and whether it should demand a token. Written out
    /// rather than derived from endpoint metadata on purpose: deriving it would re-use the
    /// transformer's own path-matching, and a bug there would cancel out instead of failing.
    /// Adding an endpoint means adding a line here.
    /// </summary>
    private static readonly (string Path, string Method, bool Protected)[] Operations =
    [
        ("/",                             "get",    false),
        ("/health",                       "get",    false),
        ("/auth/register",                "post",   false),
        ("/auth/login",                   "post",   false),
        ("/resources",                    "get",    true),
        ("/resources",                    "post",   true),
        ("/resources/{id}",               "get",    true),
        ("/resources/{id}/availability",  "get",    true),
        ("/reservations",                 "post",   true),
        ("/reservations",                 "get",    true),
        ("/reservations/{id}",            "delete", true),
        ("/maintenance-windows",          "post",   true),
        ("/users/{id}/certifications",    "post",   true)
    ];

    [Fact]
    public async Task The_document_is_served_outside_development()
    {
        Assert.NotEqual("Development", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));

        var response = await fixture.Anonymous.GetAsync(DocumentPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_document_declares_a_jwt_bearer_scheme()
    {
        using var document = await GetDocumentAsync();

        var scheme = document.RootElement
            .GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("Bearer");

        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
    }

    /// <summary>
    /// The one that matters. Two failures hide here and both render a page that looks finished:
    /// an OpenApiSecuritySchemeReference built in an operation transformer serialises as an
    /// empty "security": [{}] on .NET 10, and matching ApiDescription.RelativePath without
    /// stripping route constraints silently skips every parameterised route.
    /// </summary>
    [Fact]
    public async Task Protected_operations_require_the_bearer_scheme_and_anonymous_ones_do_not()
    {
        using var document = await GetDocumentAsync();
        var paths = document.RootElement.GetProperty("paths");

        var wrong = new List<string>();

        foreach (var (path, method, isProtected) in Operations)
        {
            Assert.True(paths.TryGetProperty(path, out var item), $"{path} is missing from the document");
            Assert.True(item.TryGetProperty(method, out var operation), $"{method.ToUpperInvariant()} {path} is missing");

            var names = operation.TryGetProperty("security", out var security)
                ? security.EnumerateArray()
                          .SelectMany(requirement => requirement.EnumerateObject())
                          .Select(scheme => scheme.Name)
                          .ToList()
                : [];

            var satisfied = isProtected ? names.Contains("Bearer") : names.Count == 0;

            if (!satisfied)
            {
                wrong.Add($"{method.ToUpperInvariant()} {path}: expected "
                          + (isProtected ? "Bearer" : "no security")
                          + $", document has [{string.Join(", ", names)}]");
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    /// <summary>
    /// Serving HTML is not enough to call this working. The page is a shell that fetches the
    /// document by a URL baked into its bootstrap config, and if that URL stops resolving the
    /// page still returns 200 and simply renders nothing. So follow it.
    /// </summary>
    [Fact]
    public async Task The_docs_page_is_served_and_points_at_a_document_that_resolves()
    {
        var response = await fixture.Anonymous.GetAsync("/docs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var html = await response.Content.ReadAsStringAsync();
        var source = Regex.Match(html, @"""sources"":\[\{[^\]]*?""url"":""(?<url>[^""]+)""");
        Assert.True(source.Success, $"No OpenAPI source URL in the docs page:{Environment.NewLine}{html}");

        // Scalar resolves a relative source against the origin plus its base path, which is
        // empty when the app is served from the root - so this is the URL the browser fetches.
        var documentUrl = new Uri(new Uri("http://localhost/"), source.Groups["url"].Value).AbsolutePath;
        Assert.Equal(HttpStatusCode.OK, (await fixture.Anonymous.GetAsync(documentUrl)).StatusCode);
    }


    [Fact]
    public async Task The_root_sends_a_browser_to_the_docs_and_leaves_a_terminal_its_json()
    {
        // The bare host is the URL people paste, and a browser landing on JSON puts the
        // reference one undiscoverable hop away. curl keeps the JSON: it is the useful
        // answer there, and the landing document is the machine-readable entry point.
        // The client follows redirects, so this asserts where a browser ends up rather than
        // the 302 itself - which is the behaviour that matters.
        using var browser = new HttpRequestMessage(HttpMethod.Get, "/");
        browser.Headers.Add("Accept", "text/html,application/xhtml+xml");
        var landed = await fixture.Anonymous.SendAsync(browser);

        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
        Assert.Equal("text/html", landed.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("/docs", landed.RequestMessage!.RequestUri!.AbsolutePath);

        using var terminal = new HttpRequestMessage(HttpMethod.Get, "/");
        terminal.Headers.Add("Accept", "*/*");
        var json = await fixture.Anonymous.SendAsync(terminal);

        Assert.Equal(HttpStatusCode.OK, json.StatusCode);
        Assert.Equal("application/json", json.Content.Headers.ContentType?.MediaType);
    }
    private async Task<JsonDocument> GetDocumentAsync()
        => JsonDocument.Parse(await fixture.Anonymous.GetStringAsync(DocumentPath));
}
