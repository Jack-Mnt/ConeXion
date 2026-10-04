# Pruebas reales — ConeXion 2.0.1

## 1. Inicio y catálogo V2

- abrir ConeXion con una instalación aprovisionada;
- comprobar sede correcta;
- comprobar versión `2.0.1`;
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

## 3. Incidencias parciales

Probar de forma controlada:

- `producto_ausente` → `eliminados[]` con motivo correspondiente;
- `codigo_interno_duplicado` conocido → no `stock[]`, sí `eliminados[]` e incidencia;
- `stock_invalido` conocido → no `stock[]`, sí `eliminados[]` e incidencia;
- `codigo_interno_invalido` → incidencia con `c_interno = null`, sin inferir identidad;
- barcode agregado/eliminado/modificado;
- precio `1.5` vs `1.50` → sin `precio_modificado`.

## 4. Sincronización

- `OK` → `Synced`;
- `DUPLICATE` → `Synced`, sin duplicar efectos;
- pérdida de red → permanece `Pending`;
- `SNAPSHOT_WINDOW_NOT_OPEN` / captura futura → `Pending`;
- error contractual permanente → `Failed`;
- pendientes se reintentan cronológicamente solo en el siguiente evento de sincronización.

## 5. Ventana de 2 horas

Verificar que ConeXion impida una nueva captura cuando todavía no corresponde y que el backend siga siendo autoritativo.
