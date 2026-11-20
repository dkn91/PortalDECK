using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PortalDeck;

internal sealed class UsbTunnel(string configPath) : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private Task? worker;
    private int connected;
    public bool Connected => Volatile.Read(ref connected) != 0;

    public void Start() => worker = Task.Run(RepairLoop);

    public void Reconnect()
    {
        if (wake.CurrentCount == 0) wake.Release();
    }

    private async Task RepairLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Repair();
                Volatile.Write(ref connected, 1);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException
                    or System.ComponentModel.Win32Exception or OperationCanceledException or UnauthorizedAccessException)
            {
                Volatile.Write(ref connected, 0);
            }

            try { await wake.WaitAsync(TimeSpan.FromSeconds(5), stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task Repair()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(configPath));
        if (config.RootElement.ValueKind != JsonValueKind.Object ||
            !config.RootElement.TryGetProperty("adbPath", out var adbPath) || adbPath.ValueKind != JsonValueKind.String ||
            !config.RootElement.TryGetProperty("serial", out var device) || device.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException();
        var adb = adbPath.GetString()!;
        var serial = device.GetString()!;
        if (!Path.IsPathFullyQualified(adb) || !File.Exists(adb) || string.IsNullOrWhiteSpace(serial))
            throw new InvalidOperationException();

        if (!string.Equals((await Adb(adb, "-d", "get-serialno")).Trim(), serial, StringComparison.Ordinal))
            throw new InvalidOperationException();

        // Pin this transport for the entire repair; a reconnect will assign a new ID.
        var transports = (await Adb(adb, "devices", "-l")).Split('\n')
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length >= 3 && fields[0] == serial && fields[1] == "device")
            .ToArray();
        if (transports.Length != 1)
            throw new InvalidOperationException();
        const string prefix = "transport_id:";
        var id = transports[0].FirstOrDefault(field => field.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        if (id is null || !uint.TryParse(id, out _))
            throw new InvalidOperationException();

        const string endpoint = "tcp:8731";
        var mappings = await Adb(adb, "-t", id, "reverse", "--list");
        if (!mappings.Split('\n').Any(line =>
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return fields.Length == 3 && fields[1] == endpoint && fields[2] == endpoint;
        }))
            await Adb(adb, "-t", id, "reverse", endpoint, endpoint);
    }

    private async Task<string> Adb(string adb, params string[] args)
    {
        var info = new ProcessStartInfo(adb) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        // Do not honor an inherited setting that redirects commands to a nonlocal ADB server.
        info.Environment.Remove("ADB_SERVER_SOCKET");
        info.Environment.Remove("ANDROID_ADB_SERVER_ADDRESS");
        info.Environment.Remove("ANDROID_ADB_SERVER_PORT");
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await stderr;
            var output = await stdout;
            if (process.ExitCode != 0) throw new InvalidOperationException();
            return output;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        worker?.GetAwaiter().GetResult();
        stop.Dispose();
        wake.Dispose();
    }
}
