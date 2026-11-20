using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PortalDeck;

internal static class DeckServer
{
    public const int Port = 8731;
    public const string Host = "127.0.0.1:8731";
    public const string Origin = "http://" + Host;
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "mic", "outlook", "teams", "chrome", "edge", "youtube", "terminal", "camera", "lock"
    };

    public static WebApplication Create(string token, Func<object> state, Func<string, ActionResult> run,
        int port = Port)
    {
        var expected = Encoding.ASCII.GetBytes("Bearer " + token);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Production
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, port);
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = 512;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
        });
        var app = builder.Build();
        var actionGate = new SemaphoreSlim(1);
        app.Run(async context =>
        {
            var request = context.Request;
            var response = context.Response;
            var host = $"127.0.0.1:{context.Connection.LocalPort}";
            var origin = $"http://{host}";
            response.Headers.CacheControl = "no-store";
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["X-Frame-Options"] = "DENY";
            response.Headers.ContentSecurityPolicy =
                "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-src 'none'; frame-ancestors 'none'";
            response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

            if (!string.Equals(request.Host.Value, host, StringComparison.Ordinal))
            {
                response.StatusCode = 400;
                return;
            }

            var path = request.Path.Value;
            var isState = path == "/api/state";
            var isAction = path == "/api/action";
            if ((request.Headers.TryGetValue("Origin", out var origins) &&
                 (origins.Count != 1 || origins[0] != origin)) ||
                (request.Headers.TryGetValue("Sec-Fetch-Site", out var sites) &&
                 (sites.Count != 1 || sites[0] != "same-origin" && sites[0] != "none")) ||
                (isAction && HttpMethods.IsPost(request.Method) && origins.Count == 0))
            {
                response.StatusCode = 403;
                return;
            }

            var allowedMethod = isAction ? "POST" : "GET";
            if (request.Method != allowedMethod)
            {
                response.Headers.Allow = allowedMethod;
                response.StatusCode = 405;
                return;
            }

            if (!isState && !isAction)
            {
                var (asset, type) = path switch
                {
                    "/" => ("index.html", "text/html"),
                    "/deck.css" => ("deck.css", "text/css"),
                    "/deck.js" => ("deck.js", "text/javascript"),
                    _ => ("", "")
                };
                using var contents = typeof(DeckServer).Assembly.GetManifestResourceStream("Web." + asset);
                if (contents is null)
                {
                    response.StatusCode = 404;
                    return;
                }
                response.ContentType = type + "; charset=utf-8";
                await contents.CopyToAsync(response.Body, context.RequestAborted);
                return;
            }

            var authorization = request.Headers.Authorization;
            if (authorization.Count != 1 || authorization[0] is not string value || value.Length != expected.Length ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), expected))
            {
                response.StatusCode = 401;
                return;
            }

            if (isState)
            {
                await response.WriteAsJsonAsync(state(), context.RequestAborted);
                return;
            }

            if (!string.Equals(request.ContentType?.Split(';', 2)[0].Trim(), "application/json",
                    StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 415;
                return;
            }

            string? action = null;
            try
            {
                using var json = await JsonDocument.ParseAsync(request.Body,
                    new JsonDocumentOptions { MaxDepth = 4 }, context.RequestAborted);
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var fields = json.RootElement.EnumerateObject();
                    if (fields.MoveNext() && fields.Current.NameEquals("action") &&
                        fields.Current.Value.ValueKind == JsonValueKind.String)
                    {
                        action = fields.Current.Value.GetString();
                        if (fields.MoveNext()) action = null;
                    }
                }
            }
            catch (JsonException)
            {
                response.StatusCode = 400;
                return;
            }
            catch (BadHttpRequestException ex)
            {
                response.StatusCode = ex.StatusCode;
                return;
            }

            if (action is null || !Actions.Contains(action))
            {
                response.StatusCode = 400;
                return;
            }

            if (!await actionGate.WaitAsync(0, context.RequestAborted))
            {
                await response.WriteAsJsonAsync(new ActionResult(false, "Another action is still running."));
                return;
            }

            try
            {
                var result = run(action);
                await response.WriteAsJsonAsync(result, context.RequestAborted);
            }
            finally
            {
                actionGate.Release();
            }
        });
        return app;
    }
}
