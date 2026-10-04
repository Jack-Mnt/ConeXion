#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Release = Join-Path $Root 'release'
$SourceMain = Join-Path $Release 'ConeXion'
$SourceAdmin = Join-Path $Release 'ConeXion.Admin'
$Install = 'C:\Program Files\PuertoRico\ConeXion'
$ProgramData = 'C:\ProgramData\PuertoRico\ConeXion'
$Data = Join-Path $ProgramData 'Data'
$Catalog = Join-Path $ProgramData 'Catalog'
$Runtime = Join-Path $ProgramData 'Runtime'
$V2Marker = Join-Path $Data 'contract-v2.initialized'

if (!(Test-Path (Join-Path $SourceMain 'ConeXion.exe'))) { throw 'No se encontró release\ConeXion\ConeXion.exe. Ejecuta BUILD-PILOTO.ps1 primero.' }
if (!(Test-Path (Join-Path $SourceAdmin 'ConeXion.Admin.exe'))) { throw 'No se encontró release\ConeXion.Admin\ConeXion.Admin.exe. Ejecuta BUILD-PILOTO.ps1 primero.' }

if (Test-Path $Install) { Remove-Item $Install -Recurse -Force }
New-Item -ItemType Directory -Path $Install -Force | Out-Null
New-Item -ItemType Directory -Path $Data -Force | Out-Null
New-Item -ItemType Directory -Path $Catalog -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $Runtime 'Logs') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $Runtime 'Salida') -Force | Out-Null

# Cutover inicial a contrato/schema V2.
# Se conservan config.json y credentials.dat para no reprovisionar la instalación,
# pero no se migran SQLite, snapshots ni catálogos legacy.
if (!(Test-Path $V2Marker)) {
    foreach ($name in @('conexion.db','conexion.db-wal','conexion.db-shm')) {
        $path = Join-Path $Runtime $name
        if (Test-Path $path) { Remove-Item $path -Force }
    }
    foreach ($name in @('catalog.prcatalog','catalog.previous.prcatalog','catalog.prcatalog.tmp')) {
        $path = Join-Path $Catalog $name
        if (Test-Path $path) { Remove-Item $path -Force }
        $legacyPath = Join-Path $Data $name
        if (Test-Path $legacyPath) { Remove-Item $legacyPath -Force }
    }
}

Copy-Item (Join-Path $SourceMain '*') $Install -Recurse -Force
Copy-Item (Join-Path $SourceAdmin 'ConeXion.Admin.exe') $Install -Force

# Usuarios estándar pueden modificar únicamente Runtime y Catalog.
# Data conserva config.json y credentials.dat protegidos.
& icacls.exe $Runtime /grant '*S-1-5-32-545:(OI)(CI)M' /T /C | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'No se pudieron configurar permisos de Runtime.' }
& icacls.exe $Catalog /grant '*S-1-5-32-545:(OI)(CI)M' /T /C | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'No se pudieron configurar permisos de Catalog.' }

$Shell = New-Object -ComObject WScript.Shell
$Programs = [Environment]::GetFolderPath('CommonPrograms')
$Folder = Join-Path $Programs 'Puerto Rico'
New-Item -ItemType Directory -Path $Folder -Force | Out-Null

$Shortcut = $Shell.CreateShortcut((Join-Path $Folder 'ConeXion.lnk'))
$Shortcut.TargetPath = Join-Path $Install 'ConeXion.exe'
$Shortcut.WorkingDirectory = $Install
$Shortcut.Save()

$AdminShortcut = $Shell.CreateShortcut((Join-Path $Folder 'ConeXion Admin.lnk'))
$AdminShortcut.TargetPath = Join-Path $Install 'ConeXion.Admin.exe'
$AdminShortcut.WorkingDirectory = $Install
$AdminShortcut.Save()

if (!(Test-Path $V2Marker)) {
    Set-Content -Path $V2Marker -Value 'contract_version=2;catalog_schema_version=2' -Encoding ASCII
}

Write-Host 'ConeXion 2.0.1 instalado correctamente.' -ForegroundColor Green
if (Test-Path (Join-Path $Data 'config.json')) {
    Write-Host 'Credenciales/configuración existentes conservadas. Al abrir ConeXion se descargará y confirmará el catálogo schema V2.' -ForegroundColor Green
} else {
    Write-Host 'Siguiente paso: abre ConeXion Admin y carga el TXT de la sede.'
}
