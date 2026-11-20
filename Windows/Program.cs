using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace PortalDeck;

internal static class Program
{
    private static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortalDeck");

    [STAThread]
    private static int Main(string[] args)
    {
        bool prepare = args is ["--prepare"];
        bool testMicrophones = args is ["--test-microphones"];
        bool testCamera = args is ["--test-camera"];
        try
        {
            if (prepare)
            {
                LoadToken();
                using var pairingCertificate = CompanionCertificate.Load(Data);
                File.WriteAllText(Path.Combine(Data, "companion-pin"), CompanionCertificate.Pin(pairingCertificate), Encoding.ASCII);
                return 0;
            }
            if (testCamera || testMicrophones)
            {
                var result = testCamera ? WindowsActions.TestCamera() : WindowsActions.TestMicrophones();
                var name = testCamera ? "camera-test.json" : "microphone-test.json";
                File.WriteAllText(Path.Combine(Data, name), JsonSerializer.Serialize(result));
                return result.Ok ? 0 : 1;
            }
            string? readiness = null;
            if (args is ["--ready", var id] && Guid.TryParseExact(id, "N", out var launchId))
                readiness = @"Local\PortalDeck.Ready." + launchId.ToString("N");
            else if (args.Length != 0)
                return 2;

            using var ready = readiness is null ? null : EventWaitHandle.OpenExisting(readiness);
            using var instance = new Mutex(true, @"Local\PortalDeck.Tray", out bool first);
            if (!first) return 0;

            var token = LoadToken();
            using var certificate = CompanionCertificate.Load(Data);
            var server = DeckServer.Create(token, WindowsActions.Microphones, WindowsActions.Run, certificate);
            try
            {
                server.StartAsync().GetAwaiter().GetResult();
                using var tunnel = new UsbTunnel(Path.Combine(Data, "device.json"));
                tunnel.Start();
                Forms.Application.SetHighDpiMode(Forms.HighDpiMode.SystemAware);
                Forms.Application.EnableVisualStyles();
                Forms.Application.SetCompatibleTextRenderingDefault(false);
                using var tray = new Tray(tunnel);
                ready?.Set();
                Forms.Application.Run(tray);
            }
            finally
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                server.StopAsync(deadline.Token).GetAwaiter().GetResult();
                server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            return 0;
        }
        catch (Exception)
        {
            if (!prepare && !testMicrophones && !testCamera)
                Forms.MessageBox.Show("The companion could not start or complete shutdown. Check the pairing files and whether ports 8731 or 8732 are in use.",
                    "PortalDeck", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string LoadToken()
    {
        Directory.CreateDirectory(Data);
        var path = Path.Combine(Data, "pairing-token");
        if (!File.Exists(path))
        {
            var temporary = Path.Combine(Data, Path.GetRandomFileName());
            try
            {
                File.WriteAllText(temporary, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Encoding.ASCII);
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { File.Delete(temporary); }
        }
        var token = File.ReadAllText(path, Encoding.ASCII);
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid pairing file.");
        return token;
    }

    private sealed class Tray : Forms.ApplicationContext
    {
        private readonly Forms.NotifyIcon icon;
        private readonly Forms.ContextMenuStrip menu = new();
        private readonly Forms.Timer timer = new() { Interval = 2000 };

        public Tray(UsbTunnel tunnel)
        {
            menu.Items.Add("Open dashboard", null, (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(DeckServer.LocalOrigin + "/") { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception)
                {
                    Forms.MessageBox.Show("Could not open the default browser.", "PortalDeck");
                }
            });
            menu.Items.Add("Reconnect Portal", null, (_, _) => tunnel.Reconnect());
            menu.Items.Add("Exit", null, (_, _) => ExitThread());
            icon = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application,
                Text = "PortalDeck — waiting for USB", ContextMenuStrip = menu, Visible = true };
            timer.Tick += (_, _) => icon.Text = tunnel.Connected ? "PortalDeck — USB tunnel ready" : "PortalDeck — waiting for USB";
            timer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                icon.Visible = false;
                icon.Dispose();
                menu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
