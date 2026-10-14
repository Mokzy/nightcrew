# Renders the app icon and the README's images from the built exe.
#
#   dotnet build -c Release
#   powershell -ExecutionPolicy Bypass -File build/render-assets.ps1
#
# It loads nightcrew.exe as a library and calls AssetRenderer, so the tray app's Main never runs and nothing
# touches power settings. The HTML pages in docs/assets/src are then screenshotted by headless Chrome.
# Needs Windows PowerShell 5.1 (powershell.exe), which runs on .NET Framework like Nightcrew, and Google Chrome.
param(
  [string]$Exe = "src/Nightcrew/bin/Release/net48/nightcrew.exe",
  [string]$Chrome = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
)
$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Desktop") { throw "Run this with Windows PowerShell 5.1 (powershell.exe), not pwsh." }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne "STA") { throw "Run this on an STA thread: powershell.exe -STA" }

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "docs\assets"
$sources = Join-Path $assets "src"

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assembly = [Reflection.Assembly]::LoadFrom([string](Resolve-Path (Join-Path $root $Exe)))
$renderer = $assembly.GetType("Nightcrew.App.Ui.AssetRenderer", $true)
$lantern = [Enum]::Parse($assembly.GetType("Nightcrew.Core.MarkKind", $true), "Lantern")

$renderer.GetMethod("WriteAppIcon").Invoke($null, @([string](Join-Path $root "src\Nightcrew\nightcrew.ico"))) | Out-Null
$renderer.GetMethod("WritePanels").Invoke($null, @([string]$assets, $lantern)) | Out-Null
$renderer.GetMethod("WriteMarks").Invoke($null, @([string]$sources)) | Out-Null
"Wrote nightcrew.ico, the panel images and the marks"

# name, width, height, device scale
$pages = @(
  @("banner", 1280, 400, 2),
  @("night", 1280, 400, 2),
  @("changes", 1280, 500, 2),
  @("social", 1280, 640, 1)
)
foreach ($page in $pages) {
  $name, $width, $height, $scale = $page
  $html = [Uri]::new((Join-Path $sources "$name.html")).AbsoluteUri
  $png = Join-Path $assets "$name.png"
  if (Test-Path $png) { Remove-Item $png }
  # Chrome reports progress on stderr, which Windows PowerShell would treat as an error.
  # --allow-file-access-from-files lets the page load the fonts from src/Nightcrew/Fonts.
  $ErrorActionPreference = "Continue"
  & $Chrome --headless=new --disable-gpu --hide-scrollbars --allow-file-access-from-files `
    --force-device-scale-factor=$scale --window-size="$width,$height" --virtual-time-budget=2000 `
    --screenshot="$png" $html 2>&1 | Out-Null
  $ErrorActionPreference = "Stop"
  if (-not (Test-Path $png)) { throw "Chrome didn't write $png" }
  "Wrote $name.png"
}
