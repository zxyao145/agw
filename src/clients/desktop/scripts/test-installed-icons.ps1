$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$installers = @(Get-ChildItem "release-artifacts" -Filter "*-Setup.exe")
if ($installers.Count -ne 1) { throw "Expected one Agw Windows installer." }
$applicationRoot = Join-Path $env:LOCALAPPDATA "agw_desktop"
$updater = Join-Path $applicationRoot "Update.exe"
$verifyIcon = 'import {readFileSync} from "node:fs"; import {verifyWindowsIcon} from "./scripts/verify-release-icons.mjs"; verifyWindowsIcon(readFileSync(process.argv[1]), readFileSync("assets/agw-logo.ico"), process.argv[1]);'

try {
    $installation = Start-Process -FilePath $installers[0].FullName -ArgumentList "--silent" -Wait -PassThru
    if ($installation.ExitCode -ne 0) { throw "Agw installation failed: $($installation.ExitCode)" }

    $registration = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\agw_desktop"
    $expectedHash = (Get-FileHash "assets/agw-logo.ico" -Algorithm SHA256).Hash
    $installedHash = (Get-FileHash $registration.DisplayIcon -Algorithm SHA256).Hash
    if ($installedHash -ne $expectedHash) { throw "Control Panel must display the Agw icon." }

    node --input-type=module -e $verifyIcon $updater
    if ($LASTEXITCODE -ne 0) { throw "Installed updater must contain the Agw icon." }

    $shell = New-Object -ComObject WScript.Shell
    $desktop = [Environment]::GetFolderPath("Desktop")
    $startMenu = [Environment]::GetFolderPath("Programs")
    foreach ($location in @($desktop, $startMenu)) {
        $shortcuts = @(Get-ChildItem $location -Filter "Agw Desktop.lnk" -Recurse)
        if ($shortcuts.Count -ne 1) { throw "Expected one Agw shortcut in $location." }
        $shortcut = $shell.CreateShortcut($shortcuts[0].FullName)
        $iconPath = $shortcut.IconLocation.Split(",")[0].Trim('"')
        if (-not $iconPath.StartsWith($applicationRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Agw shortcut must use its installed application icon."
        }
        node --input-type=module -e $verifyIcon $iconPath
        if ($LASTEXITCODE -ne 0) { throw "Agw shortcut must display the Agw icon." }
    }
    Write-Output "Verified installed Agw Control Panel, updater, Desktop and Start Menu icons."
} finally {
    if (Test-Path $updater) {
        $uninstallation = Start-Process -FilePath $updater -ArgumentList "--uninstall", "--silent" -Wait -PassThru
        if ($uninstallation.ExitCode -ne 0) { throw "Agw uninstallation failed: $($uninstallation.ExitCode)" }
    }
}
