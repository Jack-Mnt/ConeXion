# ConeXion 2.0.2

ConeXion transforma el Excel de stock del POS en snapshots normalizados y los sincroniza con Supabase para SOLOG.

## Contrato de esta versión

ConeXion 2.0.2 trabaja exclusivamente con:

- `catalog_schema_version = 2`
- `contract_version = 2`
- catálogo compartido con `c_interno`, `producto`, `c_barras`, `precio`
- colección separada `excluidos[]`
- snapshots con `stock[]`, `eliminados[]`, `incidencias[]` y `resumen`
- sin `ignorados[]`

No existe compatibilidad operativa con snapshots o catálogos schema 1.

## Arquitectura preservada

```text
Excel → XlsxReader → InventoryProcessor → SQLite Pending
      → SyncCoordinator → SupabaseGateway → Supabase
```

La aplicación sigue siendo event-driven: sincroniza al iniciar, antes de cargar un Excel y después de procesarlo. No usa polling ni reintentos en segundo plano.

## Catálogo

El catálogo se almacena en:

```text
C:\ProgramData\PuertoRico\ConeXion\Catalog
```

ConeXion verifica tamaño, SHA-256, `schema_version`, `catalog_version` e integridad antes de instalarlo. Una descarga no se considera activa si Supabase no confirma la versión; si existía un catálogo V2 anterior, se restaura.

## Instalación

1. Compila con `BUILD-PILOTO.ps1`.
2. Ejecuta `INSTALAR-PILOTO.ps1` como administrador.
3. Abre ConeXion.

En el primer cutover a V2, el instalador conserva `config.json` y `credentials.dat`, pero elimina SQLite/snapshots y catálogos legacy. No convierte payloads V1 a V2.

Si la instalación no estaba aprovisionada, abre **ConeXion Admin** y carga el TXT correspondiente a la sede.

## Requisitos del Excel

Columnas obligatorias:

- Nombre de Tienda
- Nombre de Almacén
- Nombre
- C. interno
- C. barras
- Precio venta
- Stock

La sede se obtiene exclusivamente de `Nombre de Almacén`.
