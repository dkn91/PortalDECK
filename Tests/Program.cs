using System.Net;
using System.Net.Security;
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
        using var certificate = LoadPairingCertificate();
        await using var app = DeckServer.Create(token,
            () => new { state = "mixed", count = 2, message = "Mixed" },
            action => { actions.Add(action); return new ActionResult(true, "Test action accepted."); }, certificate, 0, 0);
        await app.StartAsync();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            var origin = new Uri(addresses.Single(address => address.StartsWith("http://")));
            var usb = new Uri(addresses.Single(address => address.StartsWith("https://")));
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = origin };

            async Task<string> Check(int code, HttpMethod method, string path, string? auth = null,
                string? requestOrigin = null, string? body = null, string? host = null,
                string? fetchSite = null, string contentType = "application/json", bool native = false)
            {
                using var request = new HttpRequestMessage(method, path);
                if (host is not null) request.Headers.Host = host;
                if (requestOrigin is not null) request.Headers.Add("Origin", requestOrigin);
                if (fetchSite is not null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
                if (native) request.Headers.Add("X-PortalDeck-Native", "1");
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
            await Check(403, HttpMethod.Post, actionPath, token, body: mic, native: true);
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

            var pinned = CompanionCertificate.Pin(certificate);
            using var untrusted = new HttpClient(new HttpClientHandler { UseProxy = false });
            try
            {
                using var rejected = await untrusted.GetAsync(usb);
                throw new InvalidOperationException("The private certificate was unexpectedly trusted without pinning.");
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError) { }
            using var secure = new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, peer, _, errors) => peer is not null &&
                    (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0 &&
                    CompanionCertificate.Pin(peer) == pinned
            });
            async Task CheckUsb(HttpStatusCode code, bool native, string? from = null, bool authorized = true)
            {
                using var usbRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(usb, actionPath));
                if (authorized) usbRequest.Headers.Add("Authorization", "Bearer " + token);
                if (from is not null) usbRequest.Headers.Add("Origin", from);
                if (native) usbRequest.Headers.Add("X-PortalDeck-Native", "1");
                usbRequest.Content = new StringContent(mic, Encoding.UTF8, "application/json");
                using var usbResult = await secure.SendAsync(usbRequest);
                if (usbResult.StatusCode != code)
                    throw new InvalidOperationException($"Pinned HTTPS: expected {code}, got {usbResult.StatusCode}.");
            }
            await CheckUsb(HttpStatusCode.Forbidden, false);
            await CheckUsb(HttpStatusCode.Forbidden, true, "https://other.invalid");
            await CheckUsb(HttpStatusCode.Unauthorized, true, authorized: false);
            await CheckUsb(HttpStatusCode.OK, true);
            if (actions.Count != expected.Length + 1)
                throw new InvalidOperationException("Unexpected pinned HTTPS action dispatch.");
            Console.WriteLine("Loopback API and pinned HTTPS tests passed (all actions simulated).");
        }
        finally { await app.StopAsync(); }
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 LoadPairingCertificate()
    {
        var directory = Directory.CreateTempSubdirectory("portaldeck-test-");
        try
        {
            using var initial = CompanionCertificate.Load(directory.FullName);
            var reloaded = CompanionCertificate.Load(directory.FullName);
            if (reloaded.HasPrivateKey && CompanionCertificate.Pin(initial) == CompanionCertificate.Pin(reloaded))
                return reloaded;
            reloaded.Dispose();
            throw new InvalidOperationException("The private certificate or pin changed on reload.");
        }
        finally { directory.Delete(recursive: true); }
    }
}
