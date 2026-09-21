#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the Revit MCP Bridge add-in for every installed Revit 2025+.

.DESCRIPTION
    One command, no Visual Studio, no admin rights, and no .NET SDK:

        .\install.ps1

    Detects Revit under Program Files, copies the add-in to
    %APPDATA%\Autodesk\Revit\Addins\<version>\RevitMcpBridge\ and writes the .addin manifest
    next to it. Re-running is safe: the install folder is replaced and the manifest overwritten.

    By default the add-in comes from the prebuilt dist\ folder next to this script - that is
    what the npm package ships, and it is why an end user needs neither the .NET SDK nor a
    build step. The DLL there is compiled against the Revit 2025 reference assemblies and
    loads in Revit 2025, 2026 and 2027 alike, so one binary covers every supported version.
    Pass -Build to compile a fresh one from source instead; that is the contributor path and
    the only one that needs the SDK.

    Written for Windows PowerShell 5.1 as well as PowerShell 7 - deliberately no ternary
    operator, no ??, no && / ||, and no -AsHashtable.

    Every human-readable line is prefixed with "[revit-bridge] " and a fixed-width level so a
    caller can grep it. Pass -Json to get a single JSON result object as the only stdout output
    instead, which is the mode an MCP "install bridge" tool should use.

.PARAMETER RevitVersion
    Restrict the operation to one version, e.g. 2026. Without it, every detected version is used.

.PARAMETER Build
    Force a fresh dotnet build -c Release and install that output, ignoring dist\. Requires the
    .NET SDK. For contributors working on the C# side.

.PARAMETER SkipBuild
    Never build. Install the prebuilt dist\ folder, or whatever is already in bin\Release if
    dist\ is absent. This is the default behaviour whenever dist\ is present, so the switch is
    only meaningful in a source checkout with no dist\.

.PARAMETER Uninstall
    Remove the manifest and the install folder instead of installing.

.PARAMETER Json
    Emit one JSON result object as the only stdout output. Nothing else is printed.

.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -RevitVersion 2026
.EXAMPLE
    .\install.ps1 -Build
.EXAMPLE
    .\install.ps1 -Uninstall -Json
#>
[CmdletBinding()]
param(
    [string] $RevitVersion,
    [switch] $Build,
    [switch] $SkipBuild,
    [switch] $Uninstall,
    [switch] $Json
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------
# Constants. The AddInId is fixed and must never change: Revit keys the add-in on this GUID, and
# a new one would leave a stale duplicate registered.
# ---------------------------------------------------------------------------------------------
$AddInName          = 'RevitMcpBridge'
$AssemblyFileName   = 'RevitMcpBridge.dll'
$FullClassName      = 'RevitMcpBridge.BridgeApplication'
$AddInId            = '8f2c7a41-3e6b-4d19-9c05-1b7a52e4d83f'
$VendorId           = 'com.github.revit-mcp'
$VendorDescription  = 'Revit MCP Bridge - in-process HTTP bridge for the Node MCP server'
$ManifestFileName   = 'RevitMcpBridge.addin'
$MinimumRevitYear   = 2025
$ProjectFileName    = 'RevitMcpBridge.csproj'
$DistDirName        = 'dist'

# Revit ships these itself. If one ever appears in the build output it must not be copied next to
# the add-in, or Revit loads two copies of the API and every type check fails.
$RevitOwnedFiles = @(
    'RevitAPI.dll',
    'RevitAPIUI.dll',
    'AdWindows.dll',
    'UIFramework.dll',
    'RevitAPI.xml',
    'RevitAPIUI.xml',
    'RevitAPI.runtimeconfig.json',
    'RevitAPIUI.runtimeconfig.json'
)

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

$script:Log             = New-Object System.Collections.ArrayList
$script:Warnings        = New-Object System.Collections.ArrayList
$script:Errors          = New-Object System.Collections.ArrayList
$script:VersionResults  = New-Object System.Collections.ArrayList
$script:ExitCode        = 0
$script:BuildStatus     = 'skipped'
$script:OutputDir       = $null

# ---------------------------------------------------------------------------------------------
# Output helpers
#
# Writes straight to the process stdout handle rather than the PowerShell pipeline, so these can
# be called from inside a function without their text becoming that function's return value.
# ---------------------------------------------------------------------------------------------
function Write-Line {
    param(
        [string] $Level,
        [string] $Message
    )

    $line = '[revit-bridge] ' + $Level.PadRight(9) + ' ' + $Message
    $null = $script:Log.Add($line)

    if (-not $Json) {
        [Console]::Out.WriteLine($line)
    }
}

function Write-Warn {
    param([string] $Message)

    $null = $script:Warnings.Add($Message)
    Write-Line -Level 'WARN' -Message $Message
}

function Write-Err {
    param([string] $Message)

    $null = $script:Errors.Add($Message)
    Write-Line -Level 'ERROR' -Message $Message
}

# ---------------------------------------------------------------------------------------------
# Detection
# ---------------------------------------------------------------------------------------------
function Get-AutodeskRoots {
    $roots = New-Object System.Collections.ArrayList

    $candidates = @($env:ProgramW6432, $env:ProgramFiles, 'C:\Program Files')

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $root = Join-Path $candidate 'Autodesk'

        if (-not (Test-Path -LiteralPath $root)) {
            continue
        }

        if ($roots -notcontains $root) {
            $null = $roots.Add($root)
        }
    }

    return $roots.ToArray()
}

function Get-InstalledRevitVersions {
    $found = New-Object System.Collections.ArrayList
    $seen  = New-Object System.Collections.ArrayList

    foreach ($root in (Get-AutodeskRoots)) {
        $directories = @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)

        foreach ($directory in $directories) {
            if ($directory.Name -notmatch '^Revit\s+(\d{4})$') {
                continue
            }

            $year = $Matches[1]

            if ($seen -contains $year) {
                continue
            }

            $null = $seen.Add($year)

            $null = $found.Add([pscustomobject]@{
                Version   = $year
                RevitPath = $directory.FullName
                HasExe    = (Test-Path -LiteralPath (Join-Path $directory.FullName 'Revit.exe'))
            })
        }
    }

    $sorted = @($found.ToArray() | Sort-Object -Property @{ Expression = { [int] $_.Version } })
    return $sorted
}

function Get-AddinsRoot {
    $path = Join-Path $env:APPDATA 'Autodesk'
    $path = Join-Path $path 'Revit'
    $path = Join-Path $path 'Addins'
    return $path
}

# Versions that already have an add-ins folder. Used by -Uninstall so the bridge can still be
# cleaned up after the matching Revit itself has been removed.
function Get-ConfiguredVersions {
    $root = Get-AddinsRoot
    $years = New-Object System.Collections.ArrayList

    if (-not (Test-Path -LiteralPath $root)) {
        return $years.ToArray()
    }

    $directories = @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)

    foreach ($directory in $directories) {
        if ($directory.Name -match '^\d{4}$') {
            $null = $years.Add($directory.Name)
        }
    }

    return $years.ToArray()
}

# ---------------------------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------------------------

# The add-in the npm package ships: already compiled, so no SDK is needed to install it.
function Resolve-PrebuiltOutput {
    $distDir = Join-Path $ScriptRoot $DistDirName

    if (-not (Test-Path -LiteralPath (Join-Path $distDir $AssemblyFileName))) {
        return $null
    }

    return $distDir
}

function Test-DotnetAvailable {
    $command = Get-Command 'dotnet' -ErrorAction SilentlyContinue

    if ($null -eq $command) {
        return $false
    }

    return $true
}

function Invoke-Build {
    $projectPath = Join-Path $ScriptRoot $ProjectFileName

    if (-not (Test-Path -LiteralPath $projectPath)) {
        Write-Err ('Project not found: ' + $projectPath)
        return $false
    }

    # Reached only when there is no prebuilt add-in to fall back on, so name both ways out.
    if (-not (Test-DotnetAvailable)) {
        $script:BuildStatus = 'failed'
        Write-Err ('The .NET SDK is not installed (no "dotnet" on PATH) and there is no prebuilt add-in at ' + (Join-Path $ScriptRoot $DistDirName) + '. Either install the .NET SDK from https://dotnet.microsoft.com/download and re-run, or reinstall the npm package @rui.branco/revit-mcp, which ships the compiled add-in and needs no SDK at all.')
        return $false
    }

    Write-Line -Level 'BUILD' -Message 'status=started configuration=Release'

    # dotnet writes diagnostics to stderr; with ErrorActionPreference=Stop a merged 2>&1 stream
    # would throw NativeCommandError, so relax it just for the call.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = & dotnet build $projectPath -c Release --nologo 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previous

    foreach ($entry in $output) {
        $text = [string] $entry

        if ([string]::IsNullOrWhiteSpace($text)) {
            continue
        }

        Write-Line -Level 'BUILD' -Message ('| ' + $text.Trim())
    }

    if ($code -ne 0) {
        $script:BuildStatus = 'failed'
        Write-Err ('dotnet build failed with exit code ' + $code + '.')
        return $false
    }

    $script:BuildStatus = 'ok'
    Write-Line -Level 'BUILD' -Message 'status=ok'
    return $true
}

function Resolve-BuildOutput {
    $binRoot = Join-Path $ScriptRoot 'bin'

    if (-not (Test-Path -LiteralPath $binRoot)) {
        return $null
    }

    $matchesFound = @(
        Get-ChildItem -LiteralPath $binRoot -Recurse -File -Filter $AssemblyFileName -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -like '*\Release*' } |
            Sort-Object -Property LastWriteTimeUtc -Descending
    )

    if ($matchesFound.Count -eq 0) {
        return $null
    }

    return $matchesFound[0].DirectoryName
}

# ---------------------------------------------------------------------------------------------
# Install / uninstall for one version
# ---------------------------------------------------------------------------------------------
function New-AddinManifest {
    param(
        [string] $AssemblyPath
    )

    # Paths can legitimately contain & - escape before embedding in XML.
    $safePath = [System.Security.SecurityElement]::Escape($AssemblyPath)

    $lines = @(
        '<?xml version="1.0" encoding="utf-8"?>',
        '<RevitAddIns>',
        '  <AddIn Type="Application">',
        ('    <Name>' + $AddInName + '</Name>'),
        ('    <Assembly>' + $safePath + '</Assembly>'),
        ('    <AddInId>' + $AddInId + '</AddInId>'),
        ('    <FullClassName>' + $FullClassName + '</FullClassName>'),
        ('    <VendorId>' + $VendorId + '</VendorId>'),
        ('    <VendorDescription>' + $VendorDescription + '</VendorDescription>'),
        '  </AddIn>',
        '</RevitAddIns>'
    )

    return ($lines -join [Environment]::NewLine)
}

function Install-ForVersion {
    param(
        [psobject] $Target,
        [string]   $OutputDir
    )

    $version     = $Target.Version
    $addinsDir   = Join-Path (Get-AddinsRoot) $version
    $installDir  = Join-Path $addinsDir $AddInName
    $manifest    = Join-Path $addinsDir $ManifestFileName
    $assembly    = Join-Path $installDir $AssemblyFileName

    # Idempotent: drop the previous copy wholesale rather than merging into it, so a renamed or
    # removed file from an older build cannot linger.
    if (Test-Path -LiteralPath $installDir) {
        Remove-Item -LiteralPath $installDir -Recurse -Force
    }

    $null = New-Item -ItemType Directory -Path $installDir -Force

    $copied = 0

    foreach ($file in @(Get-ChildItem -LiteralPath $OutputDir -File)) {
        if ($RevitOwnedFiles -contains $file.Name) {
            Write-Warn ('Not copying ' + $file.Name + ' - Revit supplies it at runtime. Check that the Revit API PackageReference is still ExcludeAssets=runtime.')
            continue
        }

        Copy-Item -LiteralPath $file.FullName -Destination $installDir -Force
        $copied = $copied + 1
    }

    foreach ($directory in @(Get-ChildItem -LiteralPath $OutputDir -Directory)) {
        Copy-Item -LiteralPath $directory.FullName -Destination $installDir -Recurse -Force
    }

    Set-Content -LiteralPath $manifest -Value (New-AddinManifest -AssemblyPath $assembly) -Encoding utf8

    Write-Line -Level 'INSTALLED' -Message ('version=' + $version + ' files=' + $copied + ' dir="' + $installDir + '" manifest="' + $manifest + '"')

    $null = $script:VersionResults.Add([ordered]@{
        version    = $version
        revitPath  = $Target.RevitPath
        addinsDir  = $addinsDir
        installDir = $installDir
        manifest   = $manifest
        assembly   = $assembly
        files      = $copied
        status     = 'installed'
    })
}

function Uninstall-ForVersion {
    param(
        [psobject] $Target
    )

    $version    = $Target.Version
    $addinsDir  = Join-Path (Get-AddinsRoot) $version
    $installDir = Join-Path $addinsDir $AddInName
    $manifest   = Join-Path $addinsDir $ManifestFileName

    $removedDir = $false
    $removedManifest = $false

    if (Test-Path -LiteralPath $installDir) {
        Remove-Item -LiteralPath $installDir -Recurse -Force
        $removedDir = $true
    }

    if (Test-Path -LiteralPath $manifest) {
        Remove-Item -LiteralPath $manifest -Force
        $removedManifest = $true
    }

    $status = 'not-installed'

    if ($removedDir -or $removedManifest) {
        $status = 'removed'
    }

    Write-Line -Level 'REMOVED' -Message ('version=' + $version + ' status=' + $status + ' dir="' + $installDir + '" manifest="' + $manifest + '"')

    $null = $script:VersionResults.Add([ordered]@{
        version          = $version
        revitPath        = $Target.RevitPath
        addinsDir        = $addinsDir
        installDir       = $installDir
        manifest         = $manifest
        removedFolder    = $removedDir
        removedManifest  = $removedManifest
        status           = $status
    })
}

# ---------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------
function Invoke-Main {
    $action = 'install'

    if ($Uninstall) {
        $action = 'uninstall'
    }

    Write-Line -Level 'INFO' -Message ('action=' + $action + ' addin=' + $AddInName + ' minimumRevit=' + $MinimumRevitYear)

    $detected = @(Get-InstalledRevitVersions)

    foreach ($entry in $detected) {
        Write-Line -Level 'DETECTED' -Message ('version=' + $entry.Version + ' hasExe=' + $entry.HasExe + ' path="' + $entry.RevitPath + '"')
    }

    $candidates = New-Object System.Collections.ArrayList

    foreach ($entry in $detected) {
        $null = $candidates.Add($entry)
    }

    # -Uninstall also cleans versions whose Revit is gone but whose add-ins folder remains.
    if ($Uninstall) {
        foreach ($year in (Get-ConfiguredVersions)) {
            $known = @($candidates.ToArray() | Where-Object { $_.Version -eq $year })

            if ($known.Count -eq 0) {
                Write-Line -Level 'DETECTED' -Message ('version=' + $year + ' hasExe=False path="" source=addins-folder')

                $null = $candidates.Add([pscustomobject]@{
                    Version   = $year
                    RevitPath = $null
                    HasExe    = $false
                })
            }
        }
    }

    # --- honour -RevitVersion ------------------------------------------------------------------
    if (-not [string]::IsNullOrWhiteSpace($RevitVersion)) {
        $wanted = $RevitVersion.Trim()

        if ($wanted -notmatch '^\d{4}$') {
            Write-Err ('-RevitVersion must be a four-digit year such as 2026, but was "' + $RevitVersion + '".')
            $script:ExitCode = 3
            return
        }

        $selected = @($candidates.ToArray() | Where-Object { $_.Version -eq $wanted })

        if ($selected.Count -eq 0) {
            Write-Warn ('Revit ' + $wanted + ' was not found under Program Files. Continuing anyway because -RevitVersion was given explicitly; the add-ins folder does not require Revit to be installed in the default location.')

            $selected = @([pscustomobject]@{
                Version   = $wanted
                RevitPath = $null
                HasExe    = $false
            })
        }
    }
    else {
        if ($candidates.Count -eq 0) {
            if ($Uninstall) {
                Write-Err 'Nothing to uninstall: no Revit installation was found under Program Files\Autodesk (looked for folders named "Revit <year>") and no add-ins folder exists under %APPDATA%\Autodesk\Revit\Addins. Pass -RevitVersion <year> to target a version explicitly.'
            }
            else {
                Write-Err 'No Revit installation was found under Program Files\Autodesk (looked for folders named "Revit <year>"). Install Revit, or pass -RevitVersion <year> to target a version explicitly.'
            }

            $script:ExitCode = 2
            return
        }

        $selected = @($candidates.ToArray())
    }

    # --- .NET Framework gate --------------------------------------------------------------------
    # Revit 2024 and earlier run add-ins on .NET Framework 4.8; this project targets net8.0-windows
    # and simply cannot be loaded by them. Installing anyway would produce a confusing load error
    # inside Revit instead of a clear message here.
    #
    # The gate applies to installs only. -Uninstall is allowed to clean any version, because a
    # leftover folder under a 2024 add-ins directory should still be removable.
    $supported = New-Object System.Collections.ArrayList

    foreach ($entry in $selected) {
        if ((-not $Uninstall) -and ([int] $entry.Version -lt $MinimumRevitYear)) {
            Write-Warn ('Revit ' + $entry.Version + ' runs add-ins on .NET Framework 4.8 and cannot load this build (it targets net8.0-windows). Revit ' + $MinimumRevitYear + ' or newer is required.')

            $null = $script:VersionResults.Add([ordered]@{
                version = $entry.Version
                status  = 'skipped-unsupported'
                reason  = 'Revit ' + $entry.Version + ' is .NET Framework based; this build requires Revit ' + $MinimumRevitYear + '+.'
            })

            continue
        }

        $null = $supported.Add($entry)
    }

    if ($supported.Count -eq 0) {
        Write-Err ('No supported Revit version to work with. This build requires Revit ' + $MinimumRevitYear + ' or newer.')
        $script:ExitCode = 3
        return
    }

    # --- uninstall --------------------------------------------------------------------------------
    if ($Uninstall) {
        foreach ($entry in $supported) {
            Uninstall-ForVersion -Target $entry
        }

        Write-Line -Level 'RESULT' -Message ('status=ok action=uninstall versions=' + (($supported.ToArray() | ForEach-Object { $_.Version }) -join ','))
        Write-Line -Level 'DONE' -Message 'Restart Revit to load the bridge'
        $script:ExitCode = 0
        return
    }

    # --- pick the add-in to install ----------------------------------------------------------------
    # Default: the prebuilt DLL in dist\, which is what the npm package ships - no .NET SDK, no
    # build step. -Build forces a fresh compile instead, for someone working on the C# side. Only a
    # source checkout with no dist\ ever falls through to building on its own.
    $outputDir = $null

    if ($Build) {
        if (-not (Invoke-Build)) {
            $script:ExitCode = 4
            return
        }

        $outputDir = Resolve-BuildOutput
    }
    else {
        $outputDir = Resolve-PrebuiltOutput

        if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
            $script:BuildStatus = 'prebuilt'
            Write-Line -Level 'BUILD' -Message 'status=skipped reason=prebuilt-dist'
        }
        elseif ($SkipBuild) {
            Write-Line -Level 'BUILD' -Message 'status=skipped reason=-SkipBuild'
            $outputDir = Resolve-BuildOutput
        }
        else {
            if (-not (Invoke-Build)) {
                $script:ExitCode = 4
                return
            }

            $outputDir = Resolve-BuildOutput
        }
    }

    if ([string]::IsNullOrWhiteSpace($outputDir)) {
        Write-Err ('Nothing to install: no prebuilt ' + $AssemblyFileName + ' in ' + (Join-Path $ScriptRoot $DistDirName) + ' and no Release build output under ' + (Join-Path $ScriptRoot 'bin') + '. Re-run with -Build to compile one (needs the .NET SDK), or reinstall the npm package @rui.branco/revit-mcp, which ships the add-in already compiled.')
        $script:ExitCode = 5
        return
    }

    $script:OutputDir = $outputDir
    Write-Line -Level 'OUTPUT' -Message ('dir="' + $outputDir + '"')

    # --- install ----------------------------------------------------------------------------------
    foreach ($entry in $supported) {
        Install-ForVersion -Target $entry -OutputDir $outputDir
    }

    Write-Line -Level 'RESULT' -Message ('status=ok action=install versions=' + (($supported.ToArray() | ForEach-Object { $_.Version }) -join ','))
    Write-Line -Level 'DONE' -Message 'Restart Revit to load the bridge'
    $script:ExitCode = 0
}

try {
    Invoke-Main
}
catch {
    Write-Err ('Unhandled failure: ' + $_.Exception.Message)

    if ($null -ne $_.ScriptStackTrace) {
        $null = $script:Log.Add('[revit-bridge] TRACE     ' + $_.ScriptStackTrace)
    }

    if ($script:ExitCode -eq 0) {
        $script:ExitCode = 1
    }
}

if ($Json) {
    $action = 'install'

    if ($Uninstall) {
        $action = 'uninstall'
    }

    $ok = $false

    if ($script:ExitCode -eq 0) {
        $ok = $true
    }

    $message = 'Restart Revit to load the bridge'

    if (-not $ok) {
        $message = 'The Revit MCP Bridge installer failed; see errors.'
    }

    $result = [ordered]@{
        ok            = $ok
        action        = $action
        exitCode      = $script:ExitCode
        message       = $message
        addInName     = $AddInName
        addInId       = $AddInId
        fullClassName = $FullClassName
        vendorId      = $VendorId
        buildStatus   = $script:BuildStatus
        outputDir     = $script:OutputDir
        versions      = @($script:VersionResults.ToArray())
        warnings      = @($script:Warnings.ToArray())
        errors        = @($script:Errors.ToArray())
        log           = @($script:Log.ToArray())
    }

    # The @(...) wrappers above matter: they keep one-element and empty collections rendering as
    # real JSON arrays under Windows PowerShell 5.1, so the caller never has to special-case them.
    # Not named $json: PowerShell variable names are case-insensitive, so that would clobber the
    # [switch] $Json parameter with a string and blow up parameter binding.
    $jsonText = ConvertTo-Json -InputObject $result -Depth 12

    [Console]::Out.WriteLine($jsonText)
}

exit $script:ExitCode
