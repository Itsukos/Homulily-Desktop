# Builds Homulily Desktop as one self-contained exe: publish\Homulily Desktop.exe (needs the .NET 9 SDK).
# To release: bump <FileVersion> in AMWin-RichPresence\AMWin-RichPresence.csproj, run this, then
#   gh release create v<version> "publish/Homulily Desktop.exe" --latest --title "v<version>" --notes "..."
# The tag must match FileVersion (with a leading v): the app's updater compares the two.

$ErrorActionPreference = "Stop"
if (Test-Path publish) { Remove-Item -Recurse -Force publish }

dotnet publish AMWin-RichPresence/AMWin-RichPresence.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o publish

$xml = [xml](Get-Content "AMWin-RichPresence\AMWin-RichPresence.csproj")
$fileVersion = ($xml.Project.PropertyGroup | Where-Object { $_.FileVersion } | Select-Object -First 1).FileVersion
Write-Host "Built Homulily Desktop v$fileVersion -> publish\Homulily Desktop.exe"
