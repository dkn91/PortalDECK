#Requires -Version 7.4
[CmdletBinding()]
param(
    [switch]$Hardware,
    [switch]$LockLast
)

$ErrorActionPreference = 'Stop'
if ($LockLast -and -not $Hardware) { throw '-LockLast requires -Hardware.' }
& dotnet run --project (Join-Path $PSScriptRoot 'Tests/ApiTests.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Loopback API tests failed.' }
$node = (Get-Command node -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
& $node --test (Join-Path $PSScriptRoot 'Tests/dashboard.test.js')
if ($LASTEXITCODE -ne 0) { throw 'Dashboard tests failed.' }
if (-not $Hardware) { return }
if ($env:OS -ne 'Windows_NT') { throw 'Hardware validation requires the deployed Windows companion and Portal.' }

$data = Join-Path $env:LOCALAPPDATA 'PortalDeck'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'PortalDeck.lnk'
if (-not (Test-Path -LiteralPath $startup)) { throw 'The Windows startup shortcut is missing. Run Deploy.ps1 first.' }
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($startup)
$exe = $shortcut.TargetPath
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'The startup shortcut points to a missing executable.' }
$device = Get-Content -LiteralPath (Join-Path $data 'device.json') -Raw | ConvertFrom-Json
$adb = $device.adbPath
$usbSerial = & $adb -d get-serialno
if ($LASTEXITCODE -ne 0 -or $usbSerial -cne $device.serial) { throw 'The configured USB Portal is not the selected physical device.' }
$mappings = @(& $adb -d reverse --list)
if ($LASTEXITCODE -ne 0 -or -not ($mappings -match '^\S+\s+tcp:8731\s+tcp:8731\s*$')) {
    throw 'The USB reverse mapping is missing.'
}
Write-Host 'Startup shortcut target and active USB reverse mapping checked. Actual sign-in startup is a manual check.'

$origin = 'http://127.0.0.1:8732'
$pairing = [IO.File]::ReadAllText((Join-Path $data 'pairing-token'))
$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$handler.AllowAutoRedirect = $false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(15)

function Invoke-Deck([string]$Action) {
    $method = if ($Action) { [Net.Http.HttpMethod]::Post } else { [Net.Http.HttpMethod]::Get }
    $path = if ($Action) { 'action' } else { 'state' }
    $request = [Net.Http.HttpRequestMessage]::new($method, "$origin/api/$path")
    try {
        $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $pairing)
        if ($Action) {
            $request.Headers.Add('Origin', $origin)
            $request.Content = [Net.Http.StringContent]::new(
                (@{action = $Action} | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
        }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            if (-not $response.IsSuccessStatusCode) { throw "Companion responded with HTTP $([int]$response.StatusCode)." }
            $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}

function Invoke-LocalCheck([string]$Name) {
    $reportPath = Join-Path $data "$Name-test.json"
    if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath }
    try {
        $argument = if ($Name -eq 'microphone') { '--test-microphones' } else { '--test-camera' }
        $test = Start-Process -FilePath $exe -ArgumentList $argument -PassThru -Wait
        $report = $null
        if (Test-Path -LiteralPath $reportPath) {
            $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
            Write-Host $report.Message
        }
        if ($test.ExitCode -ne 0 -or -not $report.Ok) { throw "Local $Name validation failed." }
    } finally {
        if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath }
    }
}

try {
    Invoke-LocalCheck 'camera'
    $before = Invoke-Deck
    Write-Host "Companion responded: microphone state $($before.state) ($($before.count) endpoints)."
    if ((Read-Host 'This briefly changes recording mute states and restores them. Test microphones now? [y/N]') -eq 'y') {
        Invoke-LocalCheck 'microphone'
        $after = Invoke-Deck
        if ($before.state -ne $after.state -or $before.count -ne $after.count) { throw 'Microphone status changed from the baseline.' }
    }
    if ((Read-Host 'Open Outlook, Teams, Chrome, Edge, YouTube and Terminal on this desktop? [y/N]') -eq 'y') {
        foreach ($action in 'outlook', 'teams', 'chrome', 'edge', 'youtube', 'terminal') {
            $result = Invoke-Deck $action
            Write-Host "${action}: $($result.message)"
            if (-not $result.ok) { throw "The $action launch was not accepted." }
        }
        if ((Read-Host 'Confirm the six apps/pages actually opened [y/N]') -ne 'y') { throw 'App launches were not confirmed.' }
    }
    if ((Read-Host 'With NO Teams meeting or pre-join screen open, check camera detection? [y/N]') -eq 'y') {
        $result = Invoke-Deck 'camera'
        Write-Host "Camera: $($result.message)"
        if ($result.ok) { throw 'Unexpected camera success; verify whether a meeting or pre-join window was present.' }
    }
    Write-Host 'Manually verify Portal rendering and touch targets, restart pairing, reconnect recovery and sign-in startup as documented.'
    if ($LockLast -and (Read-Host 'Are all other tests complete? Lock the Windows session now? [y/N]') -eq 'y') {
        $result = Invoke-Deck 'lock'
        if (-not $result.ok) { throw $result.message }
        Write-Host 'Windows accepted the lock request. Verify the lock screen; unlock normally on the PC.'
    }
} finally {
    $client.Dispose()
    Remove-Variable pairing
}
