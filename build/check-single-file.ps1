param([string]$Dir = "src/Nightcrew/bin/Release/net48")
$files = @(Get-ChildItem -Path $Dir -File)
if ($files.Count -ne 1 -or $files[0].Name -ne "nightcrew.exe") {
  Write-Error ("Expected only nightcrew.exe in $Dir, found: " + (($files | ForEach-Object Name) -join ", "))
  exit 1
}
"Single-file gate passed: $Dir\nightcrew.exe"
