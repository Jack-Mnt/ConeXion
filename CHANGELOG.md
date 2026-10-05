# Changelog — ConeXion

## 2.1.0 — Validación local previa a sincronización

- Añade una compuerta local de utilizabilidad antes de guardar el snapshot como `Pending`.
- `producto_ausente` continúa resolviéndose como stock `0` y no bloquea.
- `codigo_interno_duplicado` y `stock_invalido` quedan bloqueados porque no permiten resolver un stock teórico.
- La regla se expresa por resolubilidad de `eliminados[]`: cualquier motivo futuro sin semántica explícita de stock bloquea por defecto.
- Añade un `ResultPanel` persistente en dos columnas: errores seleccionables a la izquierda y solución contextual a la derecha.
- Reemplaza el CTA de recarga por una **X** que vuelve al panel de carga; la selección se comunica resaltando la tarjeta completa.
- Añade defensa en `SyncCoordinator` para impedir que pendientes legacy no utilizables alcancen Supabase.
- Mueve la comprobación preventiva de la ventana de dos horas después de la validación local.
- No modifica Supabase, SOLOG, Motor ni el contrato JSON V2.

## 2.0.2 — Normalización de precios Excel

- Corrige falsos `precio_modificado` causados por residuos IEEE-754 del XLSX, por ejemplo `2.2000000000000002` frente a `2.2`.
- Normaliza `Precio venta` a la precisión significativa efectiva de Excel antes de comparar o serializar.
- Añade una prueba de regresión específica para el caso detectado en `StockCutervo16-09.xlsx`.
- No modifica el contrato V2 ni el backend Supabase.

## 2.0.1 — Corrección E2E de incidencias de código de barras

- Corrige falsos `codigo_barras_modificado` cuando catálogo y Excel solo difieren por espacios internos.
- Alinea la comparación local con la validación del backend V2: el whitespace no representa un cambio real del código de barras.
- Permite reprocesar el mismo Excel si el intento local anterior quedó `Failed`; `Pending` y `Synced` continúan bloqueando duplicados.
- Añade validación automática para códigos de barras equivalentes con y sin espacios.

## 2.0.0 — Contrato ConeXion ↔ Supabase V2

- Catálogo `schema_version = 2` con `productos[]` y `excluidos[]`.
- Snapshot `contract_version = 2`.
- Eliminado `ignorados[]`.
- `producto_nuevo` viaja exclusivamente como incidencia.
- `eliminados[]` soporta `producto_ausente`, `codigo_interno_duplicado` y `stock_invalido`.
- Errores locales por SKU dejan de bloquear necesariamente todo el snapshot.
- Resumen V2 e invariantes actualizados.
- Respuestas HTTP alineadas a `codigo`.
- Comparación de precios conserva semántica decimal (`1.5 == 1.50`).
- Cutover local limpio: sin migración de SQLite/payloads schema 1.


## 1.0.0 — Primera versión estable para pruebas reales

- Intervalo mínimo autoritativo de 2 horas entre snapshots consecutivos por sede.
- Ventana calculada desde `capturado_at`.
- Supabase valida y serializa la regla por sede.
- ConeXion bloquea preventivamente nuevas capturas antes de la ventana permitida.
- Soporte de pendientes offline respetando el intervalo de 2 horas.
- Nuevos códigos: `SNAPSHOT_INTERVAL_TOO_SHORT`, `SNAPSHOT_WINDOW_NOT_OPEN`, `SNAPSHOT_CAPTURE_TIME_IN_FUTURE`.
- El estado visual distingue stock vigente y stock vencido.
- El catálogo operativo se mueve a `ProgramData\PuertoRico\ConeXion\Catalog`.
- ConeXion operativo puede descargar e instalar automáticamente nuevas versiones del catálogo.
- `Data` continúa protegido y contiene configuración/credenciales.
- Se conserva idempotencia, orden cronológico, SQLite y funcionamiento event-driven.
- No se incluye ningún catálogo dentro del paquete.
