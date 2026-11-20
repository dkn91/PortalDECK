# PortalDeck

PortalDeck turns an Android-based Facebook/Meta Portal into a USB-connected touchscreen dashboard for a Windows PC. The Portal runs a small Android app; the Windows companion serves the dashboard and performs actions on the PC. This is not a game streaming client.

## Requirements

- Windows PC and an Android-based Portal with Android API 24 or newer, USB debugging available, and a USB data cable. Enable USB debugging using the controls available for your device and approve the Windows PC on the Portal.
- [PowerShell 7.4+](https://learn.microsoft.com/powershell/scripting/install/installing-powershell) (`pwsh`), [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for Windows builds, and JDK 17 for Android builds. Set `JAVA_HOME` to the JDK or put its `bin` directory on `PATH`.
- Official [Android command-line tools](https://developer.android.com/studio#command-tools) and [Platform Tools](https://developer.android.com/tools/releases/platform-tools). Download from Google, not a third-party mirror. Where a download page publishes a SHA-256 checksum, compare it against `Get-FileHash -Algorithm SHA256 <downloaded-file>` before extracting. Use `sdkmanager` to install the following packages and review and accept the SDK licenses:

  ```text
  sdkmanager "platform-tools" "platforms;android-35" "build-tools;35.0.0"
  sdkmanager --licenses
  ```

  Set `ANDROID_HOME` to the SDK root (`ANDROID_SDK_ROOT` also works). Use the official SDK Manager package list (`sdkmanager --list_installed`) to check versions. The scripts use SDK tools already installed on your machine; they do not download them.

## Build without a device

On Windows, place the checkout in `$HOME/PortalDeck` so the Startup shortcut has a stable target. For a fresh clone:

```powershell
git clone https://github.com/dkn91/PortalDECK.git "$HOME/PortalDeck"
Set-Location "$HOME/PortalDeck"
```

From the repository root in PowerShell:

```powershell
./Build.ps1
```

This publishes a self-contained `artifacts/windows/PortalDeck.exe` for the host OS architecture (Windows x64, arm64, or x86) and builds `artifacts/android/PortalDeck.apk`. There is no Gradle or Android Studio requirement. Android builds use `aapt2`, `javac`, `d8`, `zipalign`, and `apksigner`; the script verifies the signed and aligned APK. The defaults are compile SDK 35, min SDK 24, and target SDK 28. To use another installed platform or Build Tools version, pass `-CompileSdk`, `-TargetSdk` (at least 28 and no higher than the compile SDK), or `-BuildToolsVersion`.

For Android-only builds on Linux or Windows, use `-SkipWindows`. For example, on a Linux runner with the SDK installed at `/usr/local/lib/android/sdk`:

```sh
pwsh -NoProfile -File ./Build.ps1 -SkipWindows -AndroidSdkRoot /usr/local/lib/android/sdk
```

Use `./Build.ps1 -SkipAndroid` for a Windows-only build. The Android signing key and its generated password live under `artifacts/android/` and are excluded from Git. Keep them private and keep them together: subsequent builds reuse that key so `adb install -r` can update the app. If you lose the key, uninstalling the old app before redeploying will clear its app data and pairing token.

## Deploy and pair over USB

Close any running PortalDeck Windows companion before rebuilding its executable or redeploying. Once both artifacts exist, run the following **on Windows as the account that will use PortalDeck** (no administrator shell needed):

```powershell
./Deploy.ps1
```

Connect exactly one authorized physical device. To assert which device is selected, pass `-Serial <usb-serial>`. `-AdbPath` lets you specify an official Platform Tools installation. Otherwise Deploy checks `%USERPROFILE%\Downloads\platform-tools-latest-windows\platform-tools\adb.exe` first, then the SDK's `platform-tools` directory and `PATH`. Keep the rest of the extracted Platform Tools files next to `adb.exe`.

Deploy compares `adb -d get-serialno` with the authorized row in `adb devices -l` and pins the device's transport ID when available; it does not select a Wi-Fi/TCP device or honor environment settings for a remote ADB server. If authorization fails, unlock the Portal, accept its USB debugging prompt, and retry. The script checks the API and ABI, installs or updates `app.portaldeck`, and sets up `adb reverse tcp:8731 tcp:8731`.

The Windows companion's `--prepare` mode prepares the pairing token, HTTPS certificate and `companion-pin` in `%LOCALAPPDATA%\PortalDeck`. The pin is the SHA-256 digest of the DER-encoded certificate, stored as exactly 64 uppercase ASCII hex characters. Deploy streams the token and pin separately over non-interactive adb stdin to the Android app's private `files/pairing-token` and `files/companion-pin`; neither value is passed as an argument or printed.

Deploy writes `%LOCALAPPDATA%\PortalDeck\device.json` with `adbPath` and `serial`, launches the companion, and waits up to 20 seconds for it to signal a per-launch Windows event after the listeners start. The check makes no HTTP request and does not send the pairing token. If the companion is not ready, Deploy fails without starting the Android app or creating a new Startup shortcut. On success it wakes and starts `app.portaldeck.MainActivity` to reload pairing, then creates the current user's `PortalDeck.lnk`. Keep the executable in place for the Startup shortcut to work.

**Trust boundary:** The Android APK is debuggable solely to allow `adb run-as` to provision its private token and pin over USB. An authorized debugging host can read or change all app-private data, attach a debugger, and exercise other ADB capabilities. Backups and WebView debugging are disabled. Only authorize a PC you trust, do not enable wireless debugging for this deployment, and keep the APK for local use. Keep the pairing token, certificate and private key, and Android signing material out of Git. If the companion certificate changes, redeploy over trusted USB to provision the new pin.

The Android dashboard uses pinned HTTPS at `https://127.0.0.1:8731` through the USB reverse mapping. The Android app checks the companion certificate against the provisioned pin; it does not install a system trust certificate. For the browser on the Windows PC, use `http://127.0.0.1:8732`; this separate listener is local-only and is not forwarded to the Portal. The dashboard rejects redirected action requests and blocks off-origin requests and frames.

To pair the local browser, select **Open dashboard** from the PortalDeck tray icon, then select **Pairing** to open the **Pair this browser** form. Open `%LOCALAPPDATA%\PortalDeck\pairing-token` in a local text editor, type its value into the form, and select **Connect**. Avoid displaying the token in a console, putting it in a URL, or copying it into clipboard history.

## Verify and remove

- `./Test.ps1` runs the standard checks. Use `./Test.ps1 -Hardware` on Windows for checks that require a connected and paired Portal. Add `-LockLast` to include the Windows lock check last; be prepared to unlock the PC afterward.
- On the Portal, confirm the dashboard fills the landscape screen and responds to taps at the center and near the edges of tiles across the top and bottom rows. Check that the microphone button reflects the PC's state and that each launch tile opens the intended app or page on the PC. Leave the Lock tile until last.
- Exit and restart the Android app to verify pairing persists. With the companion still running, unplug USB and verify the dashboard reports the lost connection without accepting actions. Reconnect the same authorized Portal and check that the dashboard recovers without redeploying. Sign out of Windows and sign in again to confirm the companion starts via the Startup shortcut and the dashboard reconnects.
- Camera controls the **Windows Teams meeting camera**, not the Portal camera or the system-wide camera. Test that it reports unavailable with no meeting or pre-join window. In a supported Teams meeting/pre-join window, verify a tap changes the camera indicator and tap again to restore the prior state. It only acts when it can identify a single camera control and verify its state; client updates, UI language, or multiple meeting windows can make the control unavailable.
- If an update fails with a signing mismatch, use your previous signing material or uninstall `app.portaldeck` and deploy again to re-pair.
- To remove the installation, exit the Windows companion, delete the `PortalDeck.lnk` shortcut from `shell:startup` and remove `%LOCALAPPDATA%\PortalDeck` if you no longer want the local pairing. With only the intended USB device connected, run `adb -d reverse --remove tcp:8731` and `adb -d uninstall app.portaldeck`. Disable USB debugging or revoke USB debugging authorizations on the Portal if no longer needed.
