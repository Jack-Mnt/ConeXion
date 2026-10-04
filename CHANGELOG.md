# Changelog — ConeXion

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
