# SOLOG_Logica_ConeXion_Validacion_PreSincronizacion_V1

## Estado

- Proyecto: SOLOG / ConeXion
- Tipo: delta funcional
- Clasificación: Nivel B — implementación funcional
- Estado: IMPLEMENTADO — VALIDACIÓN TÉCNICA APROBADA / SMOKE HUMANO PENDIENTE
- Baseline: `Jack-Mnt/ConeXion` · rama `main`
- HEAD previo: `f15145554d15b7fe7d0798bbb20f4d2fd060c7a7`
- Versión objetivo: ConeXion 2.1.0

## Fuente primaria

Este documento es la fuente primaria para la validación local de utilizabilidad previa a sincronización.

Para lo no reemplazado explícitamente siguen vigentes:

1. `Contrato_backend_ConeXion.md`.
2. `SOLOG_Integracion_ConeXion_Supabase_Contrato_V2.md`.
3. `SOLOG_Arquitectura_Responsabilidades_Plataformas_V1.md`.

Si existe contradicción dentro del alcance de este delta, prevalece este documento.

## Problema

Un snapshot puede cumplir el contrato V2 y, aun así, no ser utilizable por SOLOG cuando un SKU incluido queda sin stock teórico resoluble.

Actualmente:

- `producto_ausente` se resuelve operativamente como stock `0`;
- `codigo_interno_duplicado` produce stock desconocido/`NULL`;
- `stock_invalido` produce stock desconocido/`NULL`.

Cuando un grupo contiene una observación desconocida, SOLOG no puede obtener un stock agregado completo y puede bloquear el inicio del conteo.

La ventana remota de dos horas se mantiene sin cambios y solo debe consumirse cuando el snapshot enviado sea utilizable.

## Decisión funcional

ConeXion debe garantizar localmente que todos los SKU incluidos tengan stock teórico resoluble antes de persistir el snapshot como `Pending` o sincronizarlo.

Flujo:

```text
Excel
→ InventoryProcessor
→ validación local de utilizabilidad
    → no utilizable: mostrar corrección y detener
    → utilizable: continuar
→ protección de hash / ventana de 2 horas
→ SQLite Pending
→ SyncCoordinator
→ Supabase
```

La validación se repite defensivamente dentro de `SyncCoordinator` para impedir que un `Pending` legacy no utilizable alcance el gateway.

## Regla de utilizabilidad

La regla no depende de grupos, sesiones, cobertura ni Motor.

Un SKU incluido es resoluble cuando:

- aparece en `stock[]`: stock conocido;
- queda omitido normalmente: stock implícito `0`;
- aparece en `eliminados[]` con un motivo cuya semántica local resuelva un stock conocido.

Semántica vigente:

```text
producto_ausente → resuelto como 0
cualquier motivo de eliminados[] sin resolución conocida → no resoluble
```

Con el contrato V2 actual esto bloquea:

- `codigo_interno_duplicado`;
- `stock_invalido`.

La implementación no debe depender de una lista UI hardcodeada de bloqueantes; debe derivar el bloqueo de la imposibilidad de resolver stock.

## Persistencia

Si la validación previa falla:

- no llamar `SaveSnapshotAsync(... Pending)`;
- no llamar `UploadSnapshotAsync`;
- no crear snapshot remoto;
- no iniciar ni consumir una nueva ventana de dos horas;
- permitir cargar otro Excel inmediatamente.

Un `Pending` preexistente que falle la nueva validación defensiva:

```text
Pending → Failed local → no upload
```

Debe dejar de bloquear la importación de un archivo corregido.

## UX

Estado principal:

**No se puede actualizar el inventario**

Texto:

> El archivo contiene errores que impedirían iniciar un conteo en SOLOG. Corrígelos antes de continuar.

CTA principal:

**Cargar otro Excel**

No usar **Reintentar envío** para errores locales de utilizabilidad.

### codigo_interno_duplicado

Mostrar:

- código interno;
- producto;
- tipo de problema;
- filas implicadas;
- stocks/valores encontrados.

**Cómo solucionarlo**

1. Abre Tumisoft.
2. Busca el código interno afectado.
3. Corrige el código interno de los códigos duplicados.
4. Guarda los cambios y descarga el inventario nuevamente.
5. Carga el nuevo Excel en ConeXion.

### stock_invalido

Mostrar:

- código interno;
- producto;
- fila;
- valor original encontrado.

**Cómo solucionarlo**

1. Abre el archivo de inventario.
2. Ve a la fila indicada.
3. Corrige el valor de **Stock** para que sea un **NÚMERO ENTERO VÁLIDO**.
4. No elimines el producto ni cambies su código interno.
5. Guarda el archivo y vuelve a cargarlo en ConeXion.

## Fuera de alcance

No modificar:

- Supabase;
- RPC de ingestión;
- bootstrap SOLOG;
- frontend SOLOG;
- Motor de Conteos;
- política remota de dos horas;
- contrato JSON V2 enviado a Supabase.

## Casos de aceptación

A. Excel válido → se puede enviar normalmente.

B. `codigo_interno_duplicado` → bloqueo local, cero uploads, otro Excel inmediatamente.

C. `stock_invalido` → bloqueo local, cero uploads.

D. `producto_ausente` → no bloquea.

E. Excel corregido → se puede cargar inmediatamente y, si es utilizable, continuar.

F. La regla de dos horas se mantiene para snapshots válidos enviados.

G. El JSON válido conserva exactamente el contrato V2.

H. Un `Pending` legacy no utilizable nunca llega al gateway y pasa a `Failed` local.


## Implementación

Implementado en ConeXion 2.1.0.

Componentes principales:

- `SnapshotUsabilityValidator`: determina utilizabilidad por resolubilidad de `eliminados[]`.
- `MainWindow.ProcessFileAsync`: valida antes de hash/ventana/`Pending`.
- `SyncCoordinator`: revalida pendientes legacy antes del gateway y marca `Failed` local si no son utilizables.
- `ResultPanel`: muestra bloqueantes, corrección específica y CTA **Cargar otro Excel**.
- Suite `ConeXion.Validation`: cubre snapshot resoluble, producto ausente, duplicado, stock inválido y motivo futuro no resoluble.

No se modificó Supabase ni el contrato JSON V2.

### Estado de validación

Validación técnica aprobada en Windows:

- `BUILD-PILOTO.ps1` ejecutó correctamente la suite `ConeXion.Validation`.
- 19/19 validaciones aprobadas.
- `ConeXion` 2.1.0 publicado correctamente para `win-x64`.
- `ConeXion.Admin` 2.1.0 publicado correctamente para `win-x64`.
- Sin errores de compilación.

Smoke humano pendiente antes de cerrar el bloque.
