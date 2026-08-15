#!/usr/bin/env pwsh
Write-Host "SpeakerSync run script"

# Check SDK version
$sdks = dotnet --list-sdks
if ($sdks -notmatch "10\.") {
    Write-Host "Warning: .NET 10 SDK not detected. You may need to install it to target net10.0-windows." -ForegroundColor Yellow
}

dotnet run --project "$(Split-Path -Path $PSScriptRoot -Leaf)\SpeakerSync.csproj"
