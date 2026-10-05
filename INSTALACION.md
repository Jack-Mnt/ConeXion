# Instalación / actualización de ConeXion 2.1.0

## Compilar

En PowerShell:

```powershell
cd C:\Dev\ConeXion_2.1.0
.\BUILD-PILOTO.ps1
```

Se generan ejecutables self-contained `win-x64` en `release\`.

## Instalar

Abrir PowerShell como administrador:

```powershell
cd C:\Dev\ConeXion_2.1.0
.\INSTALAR-PILOTO.ps1
```

El instalador conserva las credenciales y la configuración de sede ya existentes, pero en el primer cutover a V2 elimina SQLite/snapshots y catálogos legacy. No existe migración de payloads V1.

## Primera apertura

ConeXion debe:

1. autenticarse con la instalación existente;
2. obtener el estado remoto;
3. descargar el catálogo vigente con `schema_version = 2`;
4. validar tamaño y SHA-256;
5. instalarlo y confirmarlo;
6. mostrar la sede y la versión de catálogo.

Si no hay credenciales locales, usa **ConeXion Admin** para aprovisionar mediante el TXT de la sede.
