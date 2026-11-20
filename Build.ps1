#Requires -Version 7.4
[CmdletBinding()]
param(
    [switch]$SkipWindows,
    [switch]$SkipAndroid,
    [string]$AndroidSdkRoot = $(if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { $env:ANDROID_SDK_ROOT }),
    [string]$BuildToolsVersion = '35.0.0',
    [ValidateRange(28, 999)][int]$CompileSdk = 35,
    [ValidateRange(28, 999)][int]$TargetSdk = 28
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Invoke-BuildTool([string]$Tool, [string[]]$ToolArguments) {
    & $Tool @ToolArguments
    if ($LASTEXITCODE -ne 0) {
        throw "$(Split-Path -Leaf $Tool) failed (exit $LASTEXITCODE)."
    }
}

function Get-RequiredFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file not found: $Path"
    }
    return (Get-Item -LiteralPath $Path).FullName
}

function Get-JdkTool([string]$Name) {
    $executable = if ($IsWindows) { "$Name.exe" } else { $Name }
    if ($env:JAVA_HOME) {
        return (Get-RequiredFile (Join-Path $env:JAVA_HOME "bin/$executable"))
    }
    return (Get-Command $executable -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
}

if ($SkipWindows -and $SkipAndroid) { throw 'At least one build must be selected.' }
if (-not $SkipAndroid -and $TargetSdk -gt $CompileSdk) { throw 'TargetSdk must not exceed CompileSdk.' }

$artifacts = Join-Path $PSScriptRoot 'artifacts'

if (-not $SkipWindows) {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ($arch -notin @('x64', 'arm64', 'x86')) { throw "Unsupported Windows architecture: $arch" }
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $project = Get-RequiredFile (Join-Path $PSScriptRoot 'Windows/PortalDeck.csproj')
    $output = Join-Path $artifacts 'windows'
    $publish = Join-Path $output ('.publish-' + [guid]::NewGuid().ToString('N'))
    try {
        Invoke-BuildTool $dotnet @('publish', $project, '-c', 'Release', '-r', "win-$arch",
            '--self-contained', 'true', '-p:EnableWindowsTargeting=true', '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '-o', $publish)
        Copy-Item -LiteralPath (Get-RequiredFile (Join-Path $publish 'PortalDeck.exe')) -Destination $output
        Write-Host "Built $(Join-Path $output 'PortalDeck.exe') (win-$arch)"
    } finally {
        if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
    }
}

if (-not $SkipAndroid) {
    if (-not $AndroidSdkRoot) { throw 'Set ANDROID_HOME or pass -AndroidSdkRoot. See README.md.' }
    $tools = Join-Path $AndroidSdkRoot "build-tools/$BuildToolsVersion"
    $androidJar = Get-RequiredFile (Join-Path $AndroidSdkRoot "platforms/android-$CompileSdk/android.jar")
    $exe = if ($IsWindows) { '.exe' } else { '' }
    $bat = if ($IsWindows) { '.bat' } else { '' }
    $aapt2 = Get-RequiredFile (Join-Path $tools "aapt2$exe")
    $d8 = Get-RequiredFile (Join-Path $tools "d8$bat")
    $zipalign = Get-RequiredFile (Join-Path $tools "zipalign$exe")
    $apksigner = Get-RequiredFile (Join-Path $tools "apksigner$bat")
    $javac = Get-JdkTool 'javac'
    $jar = Get-JdkTool 'jar'
    $keytool = Get-JdkTool 'keytool'

    $android = Join-Path $PSScriptRoot 'Android'
    $manifest = Get-RequiredFile (Join-Path $android 'AndroidManifest.xml')
    $output = Join-Path $artifacts 'android'
    $work = Join-Path $output ('.build-' + [guid]::NewGuid().ToString('N'))
    $gen = Join-Path $work 'gen'
    $classes = Join-Path $work 'classes'
    $dex = Join-Path $work 'dex'
    $compiledResources = Join-Path $work 'resources.zip'
    $classesJar = Join-Path $work 'classes.jar'
    $unsigned = Join-Path $work 'unsigned.apk'
    $aligned = Join-Path $work 'aligned.apk'
    $signed = Join-Path $work 'signed.apk'
    $keystore = Join-Path $output 'portaldeck-debug.jks'
    $passwordFile = Join-Path $output 'portaldeck-debug.password'

    try {
        $null = New-Item -ItemType Directory -Path $gen, $classes, $dex -Force
        $stagedManifest = Join-Path $work 'AndroidManifest.xml'
        [xml]$manifestXml = [IO.File]::ReadAllText($manifest)
        $usesSdk = $manifestXml.SelectSingleNode('/manifest/uses-sdk')
        $androidNamespace = 'http://schemas.android.com/apk/res/android'
        $null = $usesSdk.SetAttribute('minSdkVersion', $androidNamespace, '24')
        $null = $usesSdk.SetAttribute('targetSdkVersion', $androidNamespace, "$TargetSdk")
        $manifestXml.Save($stagedManifest)
        Invoke-BuildTool $aapt2 @('compile', '--dir', (Join-Path $android 'res'), '-o', $compiledResources)
        Invoke-BuildTool $aapt2 @('link', '-o', $unsigned, '-I', $androidJar, '--manifest', $stagedManifest,
            '--java', $gen, $compiledResources)
        $sources = @(Get-ChildItem -LiteralPath (Join-Path $android 'src') -Recurse -File -Filter '*.java')
        if ($sources.Count -eq 0) { throw 'No Java sources found under Android/src.' }
        $generated = @(Get-ChildItem -LiteralPath $gen -Recurse -File -Filter '*.java')
        # A javac argfile avoids the Windows command-line limit for a recursive source tree.
        $argfile = Join-Path $work 'sources.txt'
        [IO.File]::WriteAllLines($argfile, [string[]]@(($sources + $generated) | ForEach-Object {
            '"' + $_.FullName.Replace('\', '/').Replace('"', '\"') + '"'
        }))
        Invoke-BuildTool $javac @('-encoding', 'UTF-8', '--release', '8',
            '-classpath', $androidJar, '-d', $classes, "@$argfile")
        Invoke-BuildTool $jar @('--create', '--file', $classesJar, '-C', $classes, '.')
        Invoke-BuildTool $d8 @('--lib', $androidJar, '--min-api', '24', '--output', $dex, $classesJar)

        $dexFiles = @(Get-ChildItem -LiteralPath $dex -File -Filter 'classes*.dex')
        if ($dexFiles.Count -eq 0) { throw 'd8 did not produce classes.dex.' }
        $archive = [IO.Compression.ZipFile]::Open($unsigned, [IO.Compression.ZipArchiveMode]::Update)
        try {
            foreach ($file in $dexFiles) {
                $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $file.Name)
            }
        } finally {
            $archive.Dispose()
        }

        Invoke-BuildTool $zipalign @('-f', '4', $unsigned, $aligned)
        $hasKeystore = Test-Path -LiteralPath $keystore -PathType Leaf
        $hasPassword = Test-Path -LiteralPath $passwordFile -PathType Leaf
        if ($hasKeystore -ne $hasPassword) {
            throw 'The Android signing keystore and password file must be kept together. See README.md.'
        }
        if (-not $hasKeystore) {
            $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
            $newPassword = Join-Path $work 'signing.password'
            $options = [IO.FileStreamOptions]::new()
            $options.Mode = [IO.FileMode]::CreateNew
            $options.Access = [IO.FileAccess]::Write
            if (-not $IsWindows) {
                $options.UnixCreateMode = [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite
            }
            $writer = [IO.StreamWriter]::new([IO.FileStream]::new($newPassword, $options))
            try { $writer.WriteLine($password) } finally { $writer.Dispose() }
            $newKeystore = Join-Path $work 'signing.jks'
            Invoke-BuildTool $keytool @('-genkeypair', '-keystore', $newKeystore, '-storetype', 'JKS',
                '-storepass:file', $newPassword, '-keypass:file', $newPassword, '-alias', 'portaldeck-debug',
                '-keyalg', 'RSA', '-keysize', '2048', '-validity', '10000', '-dname', 'CN=PortalDeck local debug')
            Move-Item -LiteralPath $newKeystore -Destination $keystore
            Move-Item -LiteralPath $newPassword -Destination $passwordFile
        }
        Invoke-BuildTool $apksigner @('sign', '--ks', $keystore, '--ks-key-alias', 'portaldeck-debug',
            '--ks-pass', "file:$passwordFile", '--out', $signed, $aligned)
        Invoke-BuildTool $apksigner @('verify', '--verbose', $signed)
        Invoke-BuildTool $zipalign @('-c', '4', $signed)
        $apk = Join-Path $output 'PortalDeck.apk'
        Move-Item -LiteralPath $signed -Destination $apk -Force
        Write-Host "Built $apk"
    } finally {
        if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    }
}
