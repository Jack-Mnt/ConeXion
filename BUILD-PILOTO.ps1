$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Release = Join-Path $Root 'release'

Write-Host 'Ejecutando validaciones de contrato V2...'
dotnet run --project (Join-Path $Root 'src\ConeXion.Validation\ConeXion.Validation.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Fallaron las validaciones del contrato V2.' }

if (Test-Path $Release) { Remove-Item $Release -Recurse -Force }
New-Item -ItemType Directory -Path $Release | Out-Null

Write-Host 'Publicando ConeXion 2.0.1...'
dotnet restore (Join-Path $Root 'src\ConeXion\ConeXion.csproj')
dotnet publish (Join-Path $Root 'src\ConeXion\ConeXion.csproj') -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o (Join-Path $Release 'ConeXion')

Write-Host 'Publicando ConeXion Admin 2.0.1...'
dotnet restore (Join-Path $Root 'src\ConeXion.Admin\ConeXion.Admin.csproj')
dotnet publish (Join-Path $Root 'src\ConeXion.Admin\ConeXion.Admin.csproj') -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o (Join-Path $Release 'ConeXion.Admin')

Write-Host ''
Write-Host 'Compilación terminada.' -ForegroundColor Green
Write-Host "Salida: $Release"
