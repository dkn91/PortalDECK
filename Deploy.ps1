#Requires -Version 7.4
[CmdletBinding()]
param(
    [string]$Serial,
    [string]$AdbPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Run Deploy.ps1 on the Windows PC as the user who will run PortalDeck.' }

$artifacts = Join-Path $PSScriptRoot 'artifacts'
$companion = Join-Path $artifacts 'windows/PortalDeck.exe'
$apk = Join-Path $artifacts 'android/PortalDeck.apk'
foreach ($file in @($companion, $apk)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing $file. Run Build.ps1 first." }
}

if (-not $AdbPath -and $env:USERPROFILE) {
    $candidate = Join-Path $env:USERPROFILE 'Downloads/platform-tools-latest-windows/platform-tools/adb.exe'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $AdbPath = $candidate }
}
if (-not $AdbPath) {
    $sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { $env:ANDROID_SDK_ROOT }
    if ($sdk) {
        $candidate = Join-Path $sdk 'platform-tools/adb.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $AdbPath = $candidate }
    }
    if (-not $AdbPath) {
        $command = Get-Command adb.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { $AdbPath = $command.Source }
    }
}
if (-not $AdbPath -or -not (Test-Path -LiteralPath $AdbPath -PathType Leaf)) {
    throw 'Set ANDROID_HOME, put platform-tools on PATH, or provide -AdbPath pointing to adb.exe.'
}
$adb = (Get-Item -LiteralPath $AdbPath).FullName
$package = 'app.portaldeck'
$reversePort = 'tcp:8731'

function Invoke-Adb {
    param([string[]]$ToolArguments, [string]$Operation, [byte[]]$InputBytes)

    $hasInput = $PSBoundParameters.ContainsKey('InputBytes')
    $info = [Diagnostics.ProcessStartInfo]::new($adb)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardInput = $hasInput
    if ($hasInput) { $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false) }
    foreach ($name in @('ADB_SERVER_SOCKET', 'ANDROID_ADB_SERVER_ADDRESS', 'ANDROID_ADB_SERVER_PORT')) {
        $null = $info.Environment.Remove($name)
    }
    foreach ($argument in $ToolArguments) { $info.ArgumentList.Add($argument) }

    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($hasInput) {
            # Use raw bytes: no PowerShell string conversion, BOM, or added newline.
            try {
                $process.StandardInput.BaseStream.Write($InputBytes, 0, $InputBytes.Length)
                $process.StandardInput.Close()
            } catch {
                throw "$Operation failed while writing to adb stdin."
            }
        }
        if (-not $process.WaitForExit(120000)) {
            $process.Kill()
            throw "$Operation timed out."
        }
        $text = $stdout.GetAwaiter().GetResult().Trim()
        $errorText = $stderr.GetAwaiter().GetResult().Trim()
        if ($process.ExitCode -ne 0) {
            if ($hasInput) { throw "$Operation failed (adb exit $($process.ExitCode)); output withheld." }
            throw "$Operation failed (adb exit $($process.ExitCode)). $errorText $text"
        }
        if (-not $hasInput) { return $text }
    } finally {
        $process.Dispose()
    }
}

$devices = Invoke-Adb -ToolArguments @('devices', '-l') -Operation 'Listing adb devices'
$authorizeHelp = 'Connect exactly one Portal over USB, enable USB debugging, and approve this PC on the Portal.'
try {
    $usbSerial = Invoke-Adb -ToolArguments @('-d', 'get-serialno') -Operation 'Selecting USB transport'
} catch {
    throw "$authorizeHelp $($_.Exception.Message)"
}
if (-not $usbSerial -or $usbSerial -eq 'unknown' -or ($Serial -and $usbSerial -cne $Serial)) {
    throw "The requested serial does not match the confirmed USB device. $authorizeHelp"
}
$Serial = $usbSerial
$rows = @($devices -split '\r?\n')
$matchingRows = @($rows | Where-Object { ($_.Trim() -split '\s+')[0] -ceq $Serial })
if ($matchingRows.Count -ne 1 -or $matchingRows[0] -cnotmatch '^\s*\S+\s+device(?:\s|$)') {
    throw "The confirmed USB serial is not listed exactly once as authorized. $authorizeHelp"
}
$transport = @('-s', $Serial)
if ($matchingRows[0] -match '(?:^|\s)transport_id:([0-9]+)(?=\s|$)') {
    $id = $Matches[1]
    if (@($rows | Where-Object { $_ -match "(?:^|\s)transport_id:${id}(?=\s|$)" }).Count -ne 1) {
        throw 'ADB reported a non-unique transport ID. Update Platform Tools and reconnect your Portal.'
    }
    $transport = @('-t', $id)
}

function Confirm-SelectedUsb {
    $current = Invoke-Adb -ToolArguments @('-d', 'get-serialno') -Operation 'Rechecking USB transport'
    $bound = Invoke-Adb -ToolArguments ($transport + 'get-serialno') -Operation 'Checking selected transport'
    if ($current -cne $Serial -or $bound -cne $Serial) {
        throw 'USB device changed during deployment. Reconnect and run Deploy.ps1 again.'
    }
}

Confirm-SelectedUsb
$version = Invoke-Adb -ToolArguments ($transport + @('shell', 'getprop', 'ro.build.version.sdk')) -Operation 'Reading Android API'
$abi = Invoke-Adb -ToolArguments ($transport + @('shell', 'getprop', 'ro.product.cpu.abilist')) -Operation 'Reading Android ABIs'
if (-not $abi) {
    $abi = Invoke-Adb -ToolArguments ($transport + @('shell', 'getprop', 'ro.product.cpu.abi')) -Operation 'Reading Android ABI'
}
if ($version -notmatch '^\d{1,4}$' -or [int]$version -lt 24) { throw "Android API 24+ is required; found '$version'." }
if ($abi -notmatch '^[A-Za-z0-9_-]+(,[A-Za-z0-9_-]+)*$') { throw 'No usable Android ABI was reported.' }
Write-Host "Deploying to USB device $Serial (Android API $version; $abi)"

$local = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PortalDeck'
$prepared = Start-Process -FilePath $companion -ArgumentList '--prepare' -Wait -PassThru
if ($prepared.ExitCode -ne 0) { throw "PortalDeck --prepare failed (exit $($prepared.ExitCode))." }
$tokenPath = Join-Path $local 'pairing-token'
$pinPath = Join-Path $local 'companion-pin'
if (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf)) { throw 'PortalDeck --prepare did not create its pairing-token file.' }
if (-not (Test-Path -LiteralPath $pinPath -PathType Leaf)) { throw 'PortalDeck --prepare did not create its companion-pin file.' }
$token = [IO.File]::ReadAllBytes($tokenPath)
try {
    $pin = [IO.File]::ReadAllBytes($pinPath)
    if ($token.Length -ne 64 -or [Text.Encoding]::Latin1.GetString($token) -notmatch '\A[0-9a-f]{64}\z') {
        throw 'PortalDeck produced an invalid ASCII pairing-token file.'
    }
    if ($pin.Length -ne 64 -or [Text.Encoding]::Latin1.GetString($pin) -cnotmatch '\A[0-9A-F]{64}\z') {
        throw 'PortalDeck produced an invalid ASCII companion-pin file.'
    }
    Confirm-SelectedUsb
    $null = Invoke-Adb -ToolArguments ($transport + @('install', '-r', $apk)) -Operation 'Installing PortalDeck APK'
    Confirm-SelectedUsb
    $null = Invoke-Adb -ToolArguments ($transport + @('reverse', $reversePort, $reversePort)) -Operation 'Establishing USB reverse'
    $null = Invoke-Adb -ToolArguments ($transport + @('shell', '-T', "run-as $package mkdir -p files")) -Operation 'Creating app-private files directory'
    Confirm-SelectedUsb
    Invoke-Adb -ToolArguments ($transport + @('shell', '-T', "run-as $package sh -c 'umask 077; cat > files/pairing-token'")) `
        -Operation 'Provisioning app-private pairing token' -InputBytes $token
    Confirm-SelectedUsb
    Invoke-Adb -ToolArguments ($transport + @('shell', '-T', "run-as $package sh -c 'umask 077; cat > files/companion-pin'")) `
        -Operation 'Provisioning app-private companion certificate pin' -InputBytes $pin

} finally {
    [Array]::Clear($token, 0, $token.Length)
}

$config = @{ adbPath = $adb; serial = $Serial } | ConvertTo-Json -Compress
[IO.File]::WriteAllText((Join-Path $local 'device.json'), $config)
$readyId = [guid]::NewGuid().ToString('N')
$created = $false
$ready = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset,
    "Local\PortalDeck.Ready.$readyId", [ref]$created)
try {
    if (-not $created) { throw 'Could not create a unique PortalDeck readiness event.' }
    $process = Start-Process -FilePath $companion -ArgumentList '--ready', $readyId `
        -WorkingDirectory (Split-Path -Parent $companion) -PassThru
    try {
        $signaled = $ready.WaitOne([TimeSpan]::FromSeconds(20))
        if ($process.HasExited) {
            throw "PortalDeck exited during startup (exit $($process.ExitCode))."
        }
        if (-not $signaled) {
            throw 'PortalDeck did not signal readiness within 20 seconds. Check whether ports 8731 and 8732 are in use.'
        }
    } finally {
        $process.Dispose()
    }
} finally {
    $ready.Dispose()
}

Confirm-SelectedUsb
$null = Invoke-Adb -ToolArguments ($transport + @('shell', 'am', 'force-stop', $package)) -Operation 'Stopping PortalDeck Android app'
$null = Invoke-Adb -ToolArguments ($transport + @('shell', 'input', 'keyevent', 'KEYCODE_WAKEUP')) -Operation 'Waking Portal screen'
$started = Invoke-Adb -ToolArguments ($transport + @('shell', 'am', 'start', '-W', '-n', "$package/.MainActivity")) -Operation 'Starting PortalDeck Android app'
if ($started -notmatch '(?m)^Status:\s*ok\s*$') { throw "PortalDeck Android app did not report a successful launch. $started" }

$startup = [Environment]::GetFolderPath('Startup')
if (-not $startup) { throw 'Could not locate the current user Startup folder.' }
$null = New-Item -ItemType Directory -Path $startup -Force
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $startup 'PortalDeck.lnk'))
$shortcut.TargetPath = $companion
$shortcut.WorkingDirectory = Split-Path -Parent $companion
$shortcut.Save()
Write-Host 'PortalDeck paired and launched. Startup shortcut installed for the current Windows user.'
