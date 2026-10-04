$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root 'src\ConeXion.Validation\ConeXion.Validation.csproj'
Write-Host 'Validando contrato ConeXion V2...'
dotnet run --project $Project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Fallaron las validaciones del contrato V2.' }
Write-Host 'Validación de contrato V2 completada.' -ForegroundColor Green
