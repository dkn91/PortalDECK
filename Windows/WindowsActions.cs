using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Win32;

namespace PortalDeck;

internal sealed record ActionResult(bool Ok, string Message);

internal static class WindowsActions
{
    private static readonly object AudioGate = new();
    private static readonly object CameraGate = new();

    public static object Microphones()
    {
        lock (AudioGate)
        {
            if (!OperatingSystem.IsWindows())
                return MicState("unavailable", 0, "Core Audio requires Windows.");

            var count = 0;
            try
            {
                using var capture = AudioCapture.Open(out count);
                if (count == 0)
                    return MicState("unavailable", count, "No active recording endpoints.");

                var muted = capture.Endpoints.Values.Count(endpoint => endpoint.WasMuted);
                if (muted == count)
                    return MicState("muted", count, "All active recording endpoints are muted.");
                if (muted == 0)
                    return MicState("live", count, "All active recording endpoints are unmuted.");
                return MicState("mixed", count, $"{muted} of {count} active recording endpoints are muted.");
            }
            catch (Exception ex) when (IsOperationError(ex))
            {
                return MicState("unavailable", count, "Could not read the mute state of every active recording endpoint.");
            }
        }
    }

    private static object MicState(string state, int count, string message) => new { state, count, message };

    public static ActionResult Run(string action)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, "This action requires Windows.");

        try
        {
            return action switch
            {
                "mic" => ChangeMicrophones(selfTest: false),
                "outlook" => LaunchApp("Outlook", ["OUTLOOK.EXE", "olk.exe"],
                    ["Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows", "Microsoft.Office.OUTLOOK.EXE.15"]),
                "teams" => LaunchApp("Microsoft Teams", ["ms-teams.exe", "Teams.exe"],
                    ["MSTeams_8wekyb3d8bbwe!MSTeams", "com.squirrel.Teams.Teams"]),
                "chrome" => LaunchApp("Google Chrome", ["chrome.exe"], ["Chrome"]),
                "edge" => LaunchApp("Microsoft Edge", ["msedge.exe"], ["MSEdge"]),
                "terminal" => LaunchApp("Windows Terminal", ["wt.exe"],
                    ["Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe!App"]),
                "youtube" => ShellOpen("https://www.youtube.com/", "YouTube via the default browser"),
                "camera" => OnUiaThread(ToggleCamera),
                "lock" => LockWorkStation()
                    ? new(true, "Windows accepted the workstation lock request.")
                    : new(false, "Windows did not accept the workstation lock request."),
                _ => new(false, "Unknown Windows action.")
            };
        }
        catch (Exception ex) when (IsOperationError(ex))
        {
            return new(false, "Windows could not complete the action.");
        }
    }

    // CLI-only: every active endpoint is briefly unmuted, including initially muted ones.
    public static ActionResult TestMicrophones() => OperatingSystem.IsWindows()
        ? ChangeMicrophones(selfTest: true)
        : new(false, "The microphone self-test requires Windows.");

    private static ActionResult ChangeMicrophones(bool selfTest)
    {
        lock (AudioGate)
        {
            try
            {
                using var capture = AudioCapture.Open(out var count);
                if (count == 0)
                    return new(false, "No active recording endpoints.");

                // In a mixed state, mute all rather than exposing previously muted endpoints.
                var targetMuted = capture.Endpoints.Values.Any(endpoint => !endpoint.WasMuted);
                var touched = new HashSet<CaptureEndpoint>();
                var verified = false;
                var restored = false;
                try
                {
                    void Write(bool muted, bool useSnapshot)
                    {
                        foreach (var endpoint in capture.Endpoints.Values)
                        {
                            if (useSnapshot && endpoint.WasMuted == muted)
                                continue;
                            touched.Add(endpoint);
                            endpoint.Write(muted);
                        }
                    }

                    Write(targetMuted, useSnapshot: true);
                    verified = VerifyMicrophones(capture, targetMuted);
                    if (selfTest && verified)
                    {
                        Write(!targetMuted, useSnapshot: false);
                        verified = VerifyMicrophones(capture, !targetMuted);
                    }
                }
                catch (Exception ex) when (IsOperationError(ex))
                {
                    // A failed write can still have changed a device; include it in restoration.
                    verified = false;
                }
                finally
                {
                    if (selfTest || !verified)
                    {
                        foreach (var endpoint in touched)
                        {
                            try { endpoint.Write(endpoint.WasMuted); }
                            catch (Exception ex) when (IsOperationError(ex)) { }
                        }
                        restored = VerifyMicrophones(capture, targetMuted: null);
                    }
                }

                if (selfTest)
                {
                    if (!restored)
                        return new(false, "Microphone self-test could not verify restoration. Check the recording endpoints in Windows.");
                    if (!verified)
                        return new(false, "Microphone self-test could not verify both toggle directions; original states were verified.");
                    return new(true, $"Verified mute and unmute on {count} active recording endpoint(s), then restored each original state.");
                }
                if (verified)
                    return new(true, $"{count} active recording endpoint(s) {(targetMuted ? "muted" : "unmuted")}.");
                return new(false, restored
                    ? "The microphone change could not be verified; original states were verified."
                    : "The microphone change and restoration could not be verified. Check the recording endpoints in Windows.");
            }
            catch (Exception ex) when (IsOperationError(ex))
            {
                return new(false, "Could not read the mute state of every active recording endpoint. No change was attempted.");
            }
        }
    }

    private static bool VerifyMicrophones(AudioCapture before, bool? targetMuted)
    {
        try
        {
            using var after = AudioCapture.Open(out _);
            return after.Endpoints.Count == before.Endpoints.Count &&
                before.Endpoints.All(pair => after.Endpoints.TryGetValue(pair.Key, out var current) &&
                    current.WasMuted == (targetMuted ?? pair.Value.WasMuted));
        }
        catch (Exception ex) when (IsOperationError(ex))
        {
            return false;
        }
    }

    private static ActionResult LaunchApp(string name, string[] executableNames, string[] appIds)
    {
        foreach (var executable in executableNames)
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var root = RegistryKey.OpenBaseKey(hive, view);
                        using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{executable}");
                        if (key?.GetValue(null) is not string registered)
                            continue;
                        var path = Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
                        if (Path.IsPathFullyQualified(path) && File.Exists(path) &&
                            string.Equals(Path.GetFileName(path), executable, StringComparison.OrdinalIgnoreCase))
                        {
                            var result = ShellOpen(path, name);
                            if (result.Ok)
                                return result;
                        }
                    }
                    catch (Exception ex) when (IsOperationError(ex)) { }
                }
            }
        }

        var installed = InstalledStartApps(appIds);
        foreach (var id in appIds)
        {
            if (installed.Contains(id))
            {
                var result = ShellOpen($@"shell:AppsFolder\{id}", name);
                if (result.Ok)
                    return result;
            }
        }
        return new(false, $"{name} could not be launched from its Windows app registration.");
    }

    private static HashSet<string> InstalledStartApps(string[] appIds)
    {
        var allowed = new HashSet<string>(appIds, StringComparer.OrdinalIgnoreCase);
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dynamic? shell = null;
        dynamic? folder = null;
        dynamic? items = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                return installed;
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return installed;
            folder = shell.NameSpace("shell:AppsFolder");
            if (folder is null)
                return installed;
            items = folder.Items();
            if (items is null)
                return installed;

            int count = items.Count;
            for (var index = 0; index < count && installed.Count < allowed.Count; index++)
            {
                dynamic? item = null;
                try
                {
                    item = items.Item(index);
                    if (item is null)
                        continue;
                    if (item.Path is string pathId && allowed.Contains(pathId))
                        installed.Add(pathId);
                    if (item.ExtendedProperty("System.AppUserModel.ID") is string explicitId && allowed.Contains(explicitId))
                        installed.Add(explicitId);
                }
                catch (Exception ex) when (IsOperationError(ex)) { }
                finally { Release((object?)item); }
            }
        }
        catch (Exception ex) when (IsOperationError(ex)) { }
        finally
        {
            Release((object?)items);
            Release((object?)folder);
            Release((object?)shell);
        }
        return installed;
    }

    private static ActionResult ShellOpen(string target, string name)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return new(true, $"Requested launch of {name}.");
        }
        catch (Exception ex) when (IsOperationError(ex))
        {
            return new(false, $"Windows could not launch {name}.");
        }
    }

    public static object CameraState()
    {
        if (!OperatingSystem.IsWindows())
            return CameraStatus(null, "Teams camera control requires Windows.");
        return OnUiaThread(() =>
        {
            lock (CameraGate)
            {
                var camera = FindTeamsCamera();
                return camera is null ? CameraStatus(null, NoTeamsCamera) : CameraStatus(camera.IsOn,
                    $"The Teams meeting camera control reports {(camera.IsOn ? "on" : "off")}.");
            }
        });
    }

    private static object CameraStatus(bool? isOn, string message) =>
        new { state = isOn is null ? "unavailable" : isOn.Value ? "on" : "off", message };

    private const string NoTeamsCamera = "No single Teams meeting or pre-join window with a verifiable camera control was found.";

    private static ActionResult ToggleCamera()
    {
        lock (CameraGate)
        {
            var before = FindTeamsCamera();
            if (before is null)
                return new(false, NoTeamsCamera);
            return ChangeCamera(before.IsOn, () => InvokeCamera(before.Control), ReadStates);

            IEnumerable<bool?> ReadStates()
            {
                var until = Stopwatch.StartNew();
                do
                {
                    Thread.Sleep(125);
                    var current = ReadTeamsCamera(before.Window, before.ProcessId);
                    yield return current?.Surface == before.Surface ? current.IsOn : null;
                } while (until.Elapsed < TimeSpan.FromSeconds(2));
            }
        }
    }

    private static bool InvokeCamera(AutomationElement button)
    {
        if (button.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
            ((TogglePattern)toggle).Toggle();
        else if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            ((InvokePattern)invoke).Invoke();
        else
            return false;
        return true;
    }

    private static ActionResult ChangeCamera(bool? before, Func<bool> invoke, Func<IEnumerable<bool?>> readStates)
    {
        if (before is null)
            return new(false, NoTeamsCamera);
        try
        {
            if (!invoke())
                return new(false, NoTeamsCamera);
            foreach (var state in readStates())
                if (state.HasValue && state.Value != before.Value)
                    return new(true, $"The Teams meeting camera control now reports {(state.Value ? "on" : "off")}.");
        }
        catch (Exception ex) when (IsOperationError(ex))
        {
            return new(false, "Teams camera control did not confirm the action. Check the camera in Teams.");
        }
        return new(false, "Teams camera change could not be verified. Check the camera in Teams.");
    }

    // CLI-only: exercise the same verification logic without accessing Teams or camera hardware.
    public static ActionResult TestCamera()
    {
        var invocations = 0;
        bool Invoke() { invocations++; return true; }
        if (ChangeCamera(null, Invoke, () => [true]).Ok || invocations != 0 ||
            ChangeCamera(false, Invoke, () => [false, null, false]).Ok || invocations != 1 ||
            !ChangeCamera(false, Invoke, () => [false, null, true]).Ok || invocations != 2 ||
            !ChangeCamera(true, Invoke, () => [false]).Ok || invocations != 3 ||
            ChangeCamera(true, () => false, () => [false]).Ok ||
            ChangeCamera(true, () => throw new InvalidOperationException(), () => [false]).Ok ||
            CameraLabelState("Turn camera on") != false || CameraLabelState("Turn camera off") != true ||
            CameraLabelState("Camera") is not null ||
            ClassifyTeamsSurface(true, false, false) != TeamsSurface.Meeting ||
            ClassifyTeamsSurface(false, true, true) != TeamsSurface.PreJoin ||
            ClassifyTeamsSurface(false, true, false) is not null ||
            ClassifyTeamsSurface(false, false, true) is not null ||
            ClassifyTeamsSurface(true, true, true) is not null)
            return new(false, "Simulated Teams camera verification failed.");
        return new(true, "Simulated Teams camera verification passed. No Teams window or camera was accessed.");
    }

    private static TeamsCamera? FindTeamsCamera()
    {
        TeamsCamera? found = null;
        _ = EnumWindows((window, _) =>
        {
            var candidate = ReadTeamsCamera(window);
            if (candidate is null)
                return true;
            if (found is not null)
            {
                found = null; // More than one eligible window: do not guess.
                return false;
            }
            found = candidate;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static TeamsCamera? ReadTeamsCamera(IntPtr window, uint? expectedProcess = null)
    {
        try
        {
            if (!IsWindowVisible(window) || IsIconic(window) ||
                GetWindowThreadProcessId(window, out var id) == 0 ||
                (expectedProcess.HasValue && expectedProcess.Value != id))
                return null;

            using var process = Process.GetProcessById((int)id);
            if (!string.Equals(process.ProcessName, "ms-teams", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(process.ProcessName, "Teams", StringComparison.OrdinalIgnoreCase))
                return null;

            var root = AutomationElement.FromHandle(window);
            // Teams can expose Chromium control IDs through UIA. Only support the known
            // in-call toolbar or the pre-join Join+microphone cluster in the desktop app.
            // Other builds may omit/rename these IDs; never fall back to a generic button or a hotkey.
            var surface = ClassifyTeamsSurface(
                FindVisibleControls(root, "hangup-button").Count > 0,
                FindVisibleControls(root, "prejoin-join-button").Count > 0,
                FindVisibleControls(root, "toggle-mute", "microphone-button").Count > 0);
            if (surface is null)
                return null;

            var buttons = surface == TeamsSurface.Meeting
                ? FindVisibleControls(root, "video-button")
                : FindVisibleControls(root, "toggle-video", "video-button");
            if (buttons.Count != 1)
                return null;
            var button = buttons[0];
            var properties = button.Current;
            if (!properties.IsEnabled || (properties.ControlType != ControlType.Button &&
                properties.ControlType != ControlType.SplitButton &&
                (surface != TeamsSurface.PreJoin || properties.ControlType != ControlType.CheckBox)))
                return null;

            var nameState = CameraLabelState(properties.Name);
            var helpState = CameraLabelState(properties.HelpText);
            if (nameState.HasValue && helpState.HasValue && nameState != helpState)
                return null;

            var labelState = nameState ?? helpState;
            if (button.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
            {
                var toggleState = ((TogglePattern)toggle).Current.ToggleState;
                if (toggleState == ToggleState.Indeterminate)
                    return null;
                var isOn = toggleState == ToggleState.On;
                return labelState.HasValue && labelState.Value != isOn ? null : new(window, id, surface.Value, button, isOn);
            }
            return labelState is bool namedOn && button.TryGetCurrentPattern(InvokePattern.Pattern, out _)
                ? new(window, id, surface.Value, button, namedOn) : null;
        }
        catch (Exception ex) when (IsOperationError(ex)) { return null; }
    }

    private static TeamsSurface? ClassifyTeamsSurface(bool hangUp, bool preJoin, bool microphone) =>
        (hangUp, preJoin, microphone) switch
        {
            (true, false, _) => TeamsSurface.Meeting,
            (false, true, true) => TeamsSurface.PreJoin,
            _ => null
        };

    private static AutomationElementCollection FindVisibleControls(AutomationElement root, params string[] automationIds)
    {
        var ids = automationIds.Select(id => new PropertyCondition(AutomationElement.AutomationIdProperty, id)).ToArray();
        return root.FindAll(TreeScope.Descendants, new AndCondition(
            ids.Length == 1 ? ids[0] : new OrCondition(ids),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
    }

    private static bool? CameraLabelState(string? label) => label?.Trim().ToUpperInvariant() switch
    {
        "TURN CAMERA OFF" or "TURN OFF CAMERA" => true,
        "TURN CAMERA ON" or "TURN ON CAMERA" => false,
        _ => null
    };

    private static T OnUiaThread<T>(Func<T> operation)
    {
        // UI Automation should not run on the WPF window's STA thread.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
            return operation();

        T result = default!;
        Exception? error = null;
        var worker = new Thread(() =>
        {
            try { result = operation(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        worker.Join();
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    private enum TeamsSurface { Meeting, PreJoin }
    private sealed record TeamsCamera(IntPtr Window, uint ProcessId, TeamsSurface Surface, AutomationElement Control, bool IsOn);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private static bool IsOperationError(Exception ex) =>
        ex is ExternalException or InvalidOperationException or ArgumentException or IOException or
            UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or
            ElementNotAvailableException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    private sealed record CaptureEndpoint(IAudioEndpointVolume Volume, bool WasMuted)
    {
        public void Write(bool muted)
        {
            var context = Guid.Empty;
            Check(Volume.SetMute(muted, ref context));
        }
    }

    private sealed class AudioCapture : IDisposable
    {
        public Dictionary<string, CaptureEndpoint> Endpoints { get; } = new(StringComparer.Ordinal);

        public static AudioCapture Open(out int count)
        {
            count = 0;
            IMMDeviceEnumerator? enumerator = null;
            IMMDeviceCollection? collection = null;
            var capture = new AudioCapture();
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                const int captureFlow = 1;
                const uint active = 1;
                Check(enumerator.EnumAudioEndpoints(captureFlow, active, out collection));
                Check(collection.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice? device = null;
                    object? activated = null;
                    try
                    {
                        Check(collection.Item(i, out device));
                        Check(device.GetId(out var id));
                        var volumeId = typeof(IAudioEndpointVolume).GUID;
                        const uint clsctxAll = 23;
                        Check(device.Activate(ref volumeId, clsctxAll, IntPtr.Zero, out activated));
                        var volume = (IAudioEndpointVolume)activated;
                        Check(volume.GetMute(out var muted));
                        capture.Endpoints.Add(id, new(volume, muted));
                        activated = null; // Owned by capture after successful insertion.
                    }
                    finally
                    {
                        Release(activated);
                        Release(device);
                    }
                }
                return capture;
            }
            catch
            {
                capture.Dispose();
                throw;
            }
            finally
            {
                Release(collection);
                Release(enumerator);
            }
        }

        public void Dispose()
        {
            foreach (var endpoint in Endpoints.Values)
                Release(endpoint.Volume);
            Endpoints.Clear();
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        // Preserve the native vtable order, including the methods not used by PortalDeck.
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
