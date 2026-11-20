# PortalDECK

> Convert your Facebook/Meta Portal into a Steam Deck-style touchscreen dashboard that controls your Windows PC over USB/ADB.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-blue.svg)]()
[![Platform: Android](https://img.shields.io/badge/Platform-Android-green.svg)]()
[![ADB](https://img.shields.io/badge/ADB-USB%20Connection-orange.svg)]()

---

## 🎯 Overview

**PortalDECK** transforms your Meta/Facebook Portal (Android-based smart display) into a fully functional **Stream Deck-style touchscreen controller** for your Windows PC. Unlike traditional Stream Decks that connect via Wi-Fi/Bluetooth, PortalDECK uses a **direct USB/ADB connection** for ultra-low latency, reliable communication.

Inspired by the [r/FacebookPortal](https://www.reddit.com/r/FacebookPortal/) community and built with AI-assisted development (Codex, Claude Code, GitHub Copilot).

---

## ✨ Features

### 🎛️ Dashboard & Controls (9 Touch-Friendly Tiles)

| Control | Function | Details |
|---------|----------|---------|
| **🎤 Microphone** | Mute/Unmute All Mics | Reads actual Windows Core Audio states; handles mixed states; updates live if changed externally |
| **📧 Outlook** | Launch Outlook | Opens installed Outlook app (resolves via App ID, not hardcoded path) |
| **💬 Teams** | Launch Microsoft Teams | Opens installed Teams app |
| **🌐 Chrome** | Launch Google Chrome | Opens installed Chrome browser |
| **🔷 Edge** | Launch Microsoft Edge | Opens installed Edge browser |
| **▶️ YouTube** | Open YouTube | Opens `youtube.com` in default browser |
| **⌨️ Terminal** | Launch Windows Terminal | Opens installed Windows Terminal |
| **📷 Camera** | Toggle Teams Camera | Uses Windows UI Automation to target Teams meeting camera control specifically |
| **🔒 Lock PC** | Lock Windows Session | Uses supported Windows lock API (no sign out/shutdown/sleep) |

### 🖥️ Windows Companion App

- **C#/.NET Tray Application** - Runs in user session, no admin rights required
- **Loopback HTTP Service** - `127.0.0.1:8731` with ADB reverse forwarding
- **Auto-repairing ADB Tunnel** - Recovers automatically after USB reconnects
- **Security First**:
  - Cryptographically random pairing token
  - Origin validation
  - POST-only actions
  - No credentials/tokens in source, logs, or browser history
  - LAN/Internet exposure explicitly prevented
- **Tray Menu** - Open dashboard locally, reconnect Portal, exit
- **Per-user startup shortcut** - Auto-starts on Windows sign-in

### 📱 Android App (Portal)

- **WebView Shell** - Lightweight wrapper around the web dashboard
- **Minimal Permissions** - Only requests what's actually needed
- **Restricted Navigation** - Locked to companion service only
- **Secure Configuration**:
  - Cleartext HTTP limited to loopback host only
  - Unnecessary file/content access disabled
- **Persistence** - Pairing survives app restarts
- **Offline/Waiting Screen** - Shows status when disconnected
- **Auto-reconnect** - Automatically restores connection

---

## 🏗️ Architecture

```
┌─────────────────────┐     USB/ADB      ┌──────────────────────┐
│   Meta Portal       │ ◄──────────────► │    Windows PC        │
│   (Android 9+)      │   Reverse Tunnel │   (Windows 10/11)    │
│                     │                  │                      │
│  ┌───────────────┐  │   127.0.0.1:8731 │  ┌────────────────┐  │
│  │  WebView App  │  │ ◄──────────────► │  │ .NET Tray App  │  │
│  │  (Dashboard)  │  │   HTTP + Token   │  │  (Companion)   │  │
│  └───────────────┘  │                  │  └────────────────┘  │
│       ▲             │                  │         ▲            │
│       │ Touch       │                  │         │ System     │
│       └─────────────┘                  │         │ APIs       │
│                                         │  ┌──────┴──────┐    │
│  1280×800 Landscape                     │  │ Core Audio  │    │
│  Dark Theme, Large Tiles                │  │ UI Auto     │    │
│  Keep Screen Awake                      │  │ Process     │    │
└─────────────────────┘                  └────────────────┘
```

---

## 📋 Requirements

### Hardware
- **Meta/Facebook Portal** (any model: Portal, Portal+, Portal Mini, Portal Go, Portal TV)
- **Windows PC** (Windows 10/11, x64 or ARM64)
- **USB-C Cable** (data-capable, not charge-only)

### Software
- **ADB** (Android Debug Bridge) - Included in `platform-tools`
- **.NET 8 SDK** (for Windows companion)
- **Android SDK** (for APK building)
- **Java 17+** (for Android build tools)

### Portal Setup
1. Enable **Developer Options**: Settings → About → Tap "Build Number" 7 times
2. Enable **USB Debugging**: Settings → Developer Options → USB Debugging
3. Connect via USB-C, authorize the ADB connection on the Portal

---

## 🚀 Quick Start

### 1. Clone & Setup

```powershell
# Navigate to your user profile
cd $env:USERPROFILE

# Clone the repository
git clone https://github.com/dkn91/PortalDECK.git
cd PortalDECK
```

### 2. Build Everything

```powershell
# Build Windows companion, Android APK, and web assets
.\Build.ps1
```

This script:
- Inspects installed compilers/SDKs
- Downloads verified portable tooling if needed
- Builds the C# Windows companion
- Builds and signs the Android APK
- Packages web dashboard assets

### 3. Deploy to Portal

```powershell
# Install APK, establish ADB reverse tunnel, pair, wake Portal, launch dashboard
.\Deploy.ps1
```

### 4. Run Tests

```powershell
# Validate authentication, app launches, microphone, camera, lock, etc.
.\Test.ps1
```

---

## 📁 Project Structure

```
%USERPROFILE%\PortalDeck\
├── src/
│   ├── windows/           # C# .NET Windows Companion
│   │   ├── PortalDeck.Companion/
│   │   │   ├── Services/
│   │   │   │   ├── AudioService.cs      # Core Audio microphone control
│   │   │   │   ├── AppLauncherService.cs # Resolve & launch apps
│   │   │   │   ├── CameraService.cs     # UI Automation for Teams camera
│   │   │   │   ├── LockService.cs       # Windows lock API
│   │   │   │   └── PairingService.cs    # Token validation, origin check
│   │   │   ├── TrayApp.cs               # System tray with menu
│   │   │   └── Program.cs               # Entry point, HTTP server
│   │   └── PortalDeck.Companion.csproj
│   │
│   ├── android/           # Android WebView App
│   │   ├── app/
│   │   │   ├── src/main/
│   │   │   │   ├── java/com/portaldeck/
│   │   │   │   │   ├── MainActivity.kt
│   │   │   │   │   ├── PortalWebViewClient.kt
│   │   │   │   │   └── PairingManager.kt
│   │   │   │   ├── res/
│   │   │   │   │   ├── layout/activity_main.xml
│   │   │   │   │   ├── values/strings.xml
│   │   │   │   │   └── xml/network_security_config.xml
│   │   │   │   └── AndroidManifest.xml
│   │   │   └── build.gradle.kts
│   │   ├── build.gradle.kts
│   │   └── settings.gradle.kts
│   │
│   └── web/               # Dashboard Web Assets
│       ├── index.html
│       ├── styles.css
│       ├── app.js
│       ├── icons/         # SVG icons for each control
│       └── manifest.json
│
├── tools/                 # Downloaded tooling (excluded from git)
│   ├── platform-tools/    # ADB
│   ├── dotnet/            # .NET SDK (if portable)
│   └── android-sdk/       # Android SDK (if portable)
│
├── keys/                  # Generated signing keys (excluded from git)
│   ├── portal-deck.keystore
│   └── pairing-token.txt
│
├── bin/                   # Build outputs (excluded from git)
│   ├── win/
│   └── android/
│
├── Build.ps1              # Build script
├── Deploy.ps1             # Deploy script
├── Test.ps1               # Test/validation script
├── .gitignore
├── LICENSE
└── README.md
```

---

## 🔧 Configuration

### Windows Companion (`appsettings.json`)

```json
{
  "Http": {
    "Port": 8731,
    "Host": "127.0.0.1"
  },
  "Pairing": {
    "TokenLength": 32,
    "RequireHttps": false,
    "AllowedOrigins": ["http://127.0.0.1:8731"]
  },
  "Adb": {
    "Path": "%USERPROFILE%\\Downloads\\platform-tools-latest-windows\\platform-tools\\adb.exe",
    "ReversePort": 8731
  },
  "Startup": {
    "CreateShortcut": true
  }
}
```

### Android App (`network_security_config.xml`)

```xml
<network-security-config>
  <domain-config cleartextTrafficPermitted="true">
    <domain includeSubdomains="false">127.0.0.1</domain>
  </domain-config>
  <base-config cleartextTrafficPermitted="false" />
</network-security-config>
```

---

## 🎮 Usage

### Dashboard Layout

```
┌─────────────────────────────────────────────────────────────┐
│  🕐 14:32                    ● Connected                     │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│      ┌─────────────┐                                       │
│      │   🎤 MIC    │  ← Prominent, full-width              │
│      │  LIVE/MUTED │                                       │
│      └─────────────┘                                       │
│                                                             │
│  ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐          │
│  │ 📧Outlook│ │ 💬Teams │ │ 🌐Chrome│ │ 🔷Edge  │          │
│  └─────────┘ └─────────┘ └─────────┘ └─────────┘          │
│                                                             │
│  ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐          │
│  │ ▶️YouTube│ │ ⌨️Term  │ │ 📷Cam   │ │ 🔒Lock  │          │
│  └─────────┘ └─────────┘ └─────────┘ └─────────┘          │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### Tray Menu (Windows)

Right-click the PortalDECK tray icon for:
- **Open Dashboard** - Launch local web view
- **Reconnect Portal** - Re-establish ADB tunnel
- **Show Pairing Token** - Display current token (for manual pairing)
- **Exit** - Stop companion service

---

## 🔒 Security Model

| Layer | Protection |
|-------|------------|
| **Transport** | USB-only via ADB reverse tunnel (no network exposure) |
| **Authentication** | Cryptographically random 256-bit pairing token |
| **Authorization** | Origin validation + POST-only mutating endpoints |
| **Input Validation** | Allowlisted actions only (no arbitrary command execution) |
| **Token Storage** | Never in source, logs, browser history, or committed files |
| **Permissions** | Android app requests minimum required permissions |

---

## 🧪 Testing

The `Test.ps1` script validates:

- ✅ Authentication & cross-origin rejection
- ✅ App launch commands (Outlook, Teams, Chrome, Edge, Terminal, YouTube)
- ✅ Malformed request handling
- ✅ **Microphone**: Actual mute/unmute, mixed states, external change detection, state restoration
- ✅ **Camera**: Teams meeting detection, UI Automation invocation, no-meeting fallback
- ✅ **Lock PC**: Windows lock API (tested last - interrupts session)
- ✅ Portal touch rendering & button coordinates
- ✅ App restart pairing persistence
- ✅ USB tunnel recovery after disconnect/reconnect
- ✅ Companion responsiveness
- ✅ Windows sign-in startup shortcut

> **Note**: Camera toggle testing does NOT join/create real Teams meetings. The "no meeting available" behavior is explicitly tested. Lock testing is performed last as it locks the workstation.

---

## ⚠️ Limitations & Known Issues

| Limitation | Details |
|------------|---------|
| **USB Required** | Portal must remain connected via USB-C for operation |
| **Teams Camera** | Requires Teams meeting/pre-join screen open; uses UI Automation (may break on Teams updates) |
| **Microphone** | Controls all recording endpoints globally; cannot target individual devices |
| **ARM64 Windows** | Tested but less common; ensure .NET 8 ARM64 SDK available |
| **Portal Models** | Developed for 1280×800 displays; other resolutions may need CSS adjustments |
| **No Wi-Fi Mode** | Architecture is USB-first; Wi-Fi ADB not supported in this version |

---

## 🛠️ Development

### Prerequisites

```powershell
# Install .NET 8 SDK
winget install Microsoft.DotNet.SDK.8

# Install Android SDK (via Android Studio or command line tools)
# Set ANDROID_HOME environment variable

# Install Java 17+
winget install EclipseAdoptium.Temurin.17.JDK
```

### Building Manually

```powershell
# Windows Companion
cd src/windows/PortalDeck.Companion
dotnet publish -c Release -r win-x64 --self-contained true -o ../../../bin/win

# Android APK
cd src/android
./gradlew assembleRelease
# Output: app/build/outputs/apk/release/app-release.apk
```

### Adding New Controls

1. Add button to `src/web/index.html` (grid layout)
2. Add icon to `src/web/icons/`
3. Add handler in `src/web/app.js`
4. Add endpoint in Windows Companion (`Services/`)
5. Update pairing allowlist if needed
6. Add test case in `Test.ps1`

---

## 📦 Deployment Checklist

- [ ] Portal USB debugging enabled & authorized
- [ ] ADB detects device: `adb devices -l`
- [ ] Windows Companion running (tray icon visible)
- [ ] ADB reverse tunnel established: `adb reverse tcp:8731 tcp:8731`
- [ ] APK installed on Portal: `adb install -r bin/android/app-release.apk`
- [ ] Pairing completed (token validated)
- [ ] Dashboard launched on Portal
- [ ] All 9 controls tested
- [ ] Startup shortcut created
- [ ] USB cable secured

---

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/amazing-feature`
3. Commit changes: `git commit -m 'Add amazing feature'`
4. Push to branch: `git push origin feature/amazing-feature`
5. Open a Pull Request

---

## 📄 License

MIT License - see [LICENSE](LICENSE) for details.

---

## 🙏 Acknowledgments

- **r/FacebookPortal** community for inspiration and ADB unlocking guidance
- **Meta** for enabling ADB on Portal devices (2026)
- **Open Source Projects**:
  - [Immortal](https://github.com/starbrightlab/immortal) - Portal launcher & app store
  - [unmetaportal](https://github.com/fcjr/unmetaportal) - Portal conversion scripts
  - [PortalOverlays](https://github.com/GodricTM/PortalOverlays) - Floating HUD
  - [openportal](https://github.com/andronedev/openportal) - Browser-based Portal management
- **AI Coding Assistants**: Codex, Claude Code, GitHub Copilot for accelerated development

---

## 📞 Support

- **Issues**: [GitHub Issues](https://github.com/dkn91/PortalDECK/issues)
- **Discussions**: [GitHub Discussions](https://github.com/dkn91/PortalDECK/discussions)
- **Community**: [r/FacebookPortal](https://www.reddit.com/r/FacebookPortal/)

---

## 🔗 Related Projects

| Project | Description |
|---------|-------------|
| [Immortal](https://github.com/starbrightlab/immortal) | Custom launcher, photo frame, app store for Portal |
| [unmetaportal](https://github.com/fcjr/unmetaportal) | De-Facebooking & repurposing scripts |
| [PortalOverlays](https://github.com/GodricTM/PortalOverlays) | Floating HUD with back/home/recent buttons |
| [openportal](https://github.com/andronedev/openportal) | Browser-based Portal management via WebUSB |
| [portal-toolkit](https://github.com/jurax-sys/portal-toolkit) | Android app for Portal ADB management |

---

> **⚡ Pro Tip**: Keep the USB-C cable connected at all times for operation. The ADB reverse tunnel provides near-zero latency compared to Wi-Fi solutions.

---

*Built with ❤️ for the Portal hacking community. Give your Portal a second life as a powerful PC controller!*