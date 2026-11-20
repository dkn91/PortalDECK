using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace PortalDeck;

internal record ActionResult(bool Ok, string Message);

internal static class ApiTests
{
    public static async Task Main()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var actions = new List<string>();
        await using var app = DeckServer.Create(token,
            () => new { state = "mixed", count = 2, message = "Mixed" },
            action => { actions.Add(action); return new ActionResult(true, "Test action accepted."); }, 0);
        await app.StartAsync();
        try
        {
            var origin = new Uri(app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single());
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = origin };

            async Task<string> Check(int code, HttpMethod method, string path, string? auth = null,
                string? requestOrigin = null, string? body = null, string? host = null,
                string? fetchSite = null, string contentType = "application/json")
            {
                using var request = new HttpRequestMessage(method, path);
                if (host is not null) request.Headers.Host = host;
                if (requestOrigin is not null) request.Headers.Add("Origin", requestOrigin);
                if (fetchSite is not null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
                if (auth is not null) request.Headers.Add("Authorization", "Bearer " + auth);
                if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, contentType);
                using var result = await client.SendAsync(request);
                if ((int)result.StatusCode != code)
                    throw new InvalidOperationException($"{method} {path}: expected {code}, got {(int)result.StatusCode}.");
                if (result.Headers.CacheControl?.NoStore != true ||
                    result.Headers.Contains("Access-Control-Allow-Origin"))
                    throw new InvalidOperationException("Unexpected cache or CORS settings.");
                return await result.Content.ReadAsStringAsync();
            }

            var sameOrigin = origin.GetLeftPart(UriPartial.Authority);
            const string actionPath = "/api/action", statePath = "/api/state", mic = """{"action":"mic"}""";
            var page = await Check(200, HttpMethod.Get, "/");
            if (!page.Contains("/deck.js") || !page.Contains("/deck.css"))
                throw new InvalidOperationException("Dashboard assets not referenced.");
            await Check(200, HttpMethod.Get, "/deck.js");
            await Check(200, HttpMethod.Get, "/deck.css");
            await Check(404, HttpMethod.Get, "/pairing-token");
            await Check(400, HttpMethod.Get, "/", host: "not-loopback.invalid");
            await Check(401, HttpMethod.Get, statePath);
            await Check(401, HttpMethod.Post, actionPath, requestOrigin: sameOrigin, body: mic);
            await Check(401, HttpMethod.Get, statePath, new string('0', 64));
            await Check(403, HttpMethod.Get, statePath, token, "http://other.invalid");
            await Check(403, HttpMethod.Get, statePath, token, fetchSite: "cross-site");
            await Check(403, HttpMethod.Post, actionPath, token, body: mic);
            await Check(403, HttpMethod.Post, actionPath, token, "null", mic);
            await Check(403, HttpMethod.Options, actionPath, requestOrigin: "http://other.invalid");
            await Check(405, HttpMethod.Get, actionPath, token);
            await Check(415, HttpMethod.Post, actionPath, token, sameOrigin, mic, contentType: "text/plain");
            foreach (var malformed in new[] { "{", "[]", "null", """{"action":3}""", """{"action":"run"}""",
                         """{"action":"mic","action":"lock"}""", """{"action":"mic","extra":true}""" })
                await Check(400, HttpMethod.Post, actionPath, token, sameOrigin, malformed);
            await Check(413, HttpMethod.Post, actionPath, token, sameOrigin, new string(' ', 513));
            if (actions.Count != 0) throw new InvalidOperationException("A rejected request invoked an action.");

            using var state = JsonDocument.Parse(await Check(200, HttpMethod.Get, statePath, token));
            if (state.RootElement.GetProperty("state").GetString() != "mixed")
                throw new InvalidOperationException("Mixed microphone state was not returned.");
            var expected = new[] { "mic", "outlook", "teams", "chrome", "edge", "youtube", "terminal", "camera", "lock" };
            foreach (var action in expected)
            {
                using var result = JsonDocument.Parse(await Check(200, HttpMethod.Post, actionPath,
                    token, sameOrigin, JsonSerializer.Serialize(new { action })));
                if (!result.RootElement.GetProperty("ok").GetBoolean())
                    throw new InvalidOperationException("An allowed action was not acknowledged.");
            }
            if (!actions.SequenceEqual(expected))
                throw new InvalidOperationException("Unexpected action dispatch.");
            Console.WriteLine("Loopback API tests passed (all actions simulated).");
        }
        finally { await app.StopAsync(); }
    }
}
