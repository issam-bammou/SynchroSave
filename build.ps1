# Construit SynchroSave.exe (exécutable unique, aucun .NET à installer) dans le dossier .\publish
# Prérequis : SDK .NET 8 (https://dotnet.microsoft.com/download/dotnet/8.0)
$ErrorActionPreference = "Stop"
dotnet test tests/SynchroSave.Tests -c Release
dotnet publish src/SynchroSave.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o publish
Write-Host "`nTerminé : publish\SynchroSave.exe" -ForegroundColor Green
