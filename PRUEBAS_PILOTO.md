# Pruebas reales — ConeXion 2.1.0

## 1. Inicio y catálogo V2

- abrir ConeXion con una instalación aprovisionada;
- comprobar sede correcta;
- comprobar versión `2.1.0`;
- comprobar descarga automática de catálogo V6/schema 2 cuando no existe catálogo local;
- cerrar y abrir de nuevo: con catálogo/hash vigentes debe reutilizarlo correctamente.

## 2. Excel válido

Cargar un `.xlsx` real de la sede y comprobar:

- stock positivo enviado;
- stock negativo conservado;
- stock cero omitido de `stock[]`;
- excluidos omitidos completamente;
- producto nuevo solo como incidencia;
- resumen coherente.

## 3. Validación previa a sincronización

Probar de forma controlada:

- `producto_ausente` → se resuelve localmente como stock `0` y no bloquea;
- `codigo_interno_duplicado` conocido → ConeXion muestra filas/valores, bloquea localmente y no crea `Pending`;
- `stock_invalido` conocido → ConeXion muestra fila/valor, bloquea localmente y no crea `Pending`;
- tras corregir el Excel, debe poder cargarse inmediatamente;
- un `Pending` legacy con stock no resoluble debe pasar a `Failed` local sin llamar al gateway;
- `codigo_interno_invalido`, producto nuevo y cambios de nombre/precio/barcode no bloquean por sí mismos;
- precio `1.5` vs `1.50` → sin `precio_modificado`.

## 4. Sincronización

- `OK` → `Synced`;
- `DUPLICATE` → `Synced`, sin duplicar efectos;
- pérdida de red → permanece `Pending`;
- `SNAPSHOT_WINDOW_NOT_OPEN` / captura futura → `Pending`;
- error contractual permanente → `Failed`;
- pendientes se reintentan cronológicamente solo en el siguiente evento de sincronización.

## 5. Ventana de 2 horas

Verificar que:

- un Excel no utilizable se analice y bloquee localmente sin consumir una nueva ventana;
- un Excel utilizable siga respetando exactamente la ventana mínima de 2 horas;
- el backend siga siendo autoritativo para snapshots que sí llegan a sincronización.

## Regresión 2.0.2 — precio Excel

Con un archivo que contenga internamente `2.2000000000000002` pero cuyo valor de negocio sea `2.2`:

- no debe generarse `precio_modificado` contra un catálogo con precio `2.2`;
- el snapshot debe poder enviarse sin `INCIDENT_INVALID` por ese motivo;
- un cambio real, por ejemplo `2.2 → 2.5`, debe seguir generando `precio_modificado`.


## Regresión 2.1.0 — snapshot utilizable

Casos mínimos:

1. Excel válido → puede continuar a `Pending`/sincronización.
2. Código interno duplicado → bloqueo local, cero uploads.
3. Stock inválido → bloqueo local, cero uploads.
4. Producto ausente → no bloquea.
5. Excel corregido → puede cargarse inmediatamente.
6. El JSON de un snapshot válido mantiene `contract_version = 2` sin campos nuevos.
