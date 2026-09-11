# SqlArchive — diseño

**Estado:** decidido con Pedro el 7 de septiembre de 2026. Este documento es la referencia
del equipo para la Fase 2; cada paquete de trabajo se especifica contra él.

SqlArchive exporta una base SQL Server completa a un archivo legible, la restaura en otra,
y verifica que las dos coinciden. No lleva motor propio: compone
[`PeopleWorks.SqlSchemaDiff.Core`](https://www.nuget.org/packages/PeopleWorks.SqlSchemaDiff.Core)
1.7.0 para el esquema y
[`PeopleWorks.SyncJob.Core`](https://www.nuget.org/packages/PeopleWorks.SyncJob.Core)
1.0.0 para los datos.

La forma del archivo y la tabla de codificación de valores se adoptan de
[dbdumper](https://github.com/JeePeeTee/dbdumper) de JeePee (MIT), con crédito y sin
copiar su código. Lo que añadimos es lo que esa forma no puede dar por sí sola: un diff
real en vez de un conteo, y una restauración que no exige una base vacía.

---

## Para qué se usa

Los cuatro usos son reales y ninguno se puede recortar. Ordenan el trabajo, no lo dividen:

| Uso | Qué exige |
|---|---|
| **Mover una base entre servidores** | Fidelidad del esquema y velocidad. Es la base de todo lo demás. |
| **Archivar una base que se retira** | Que el archivo se lea solo, dentro de años, y se pueda verificar entonces. |
| **Copias de producción para pruebas** | Subconjunto coherente por FK y máscaras. **Fase 4**, no Fase 2. |
| **Auditar que dos bases coinciden** | `verify` apuntando a una base viva en vez de a una restaurada. Es el mismo código. |

Los tres primeros necesitan export e import antes que nada; el cuarto sale casi gratis del
manifiesto. De ahí el orden de los paquetes.

---

## El archivo

```
Ventas.sqlarchive                    (zip)
├── manifest.json
├── schema/010_schemas.sql           el mismo DDL que ejecuta el import, legible a mano
├── schema/020_types.sql
├── schema/030_sequences.sql
├── schema/035_synonyms.sql
├── schema/040_tables.sql            columnas y defaults; llaves e índices van después
├── schema/050_indexes.sql
├── schema/060_checks.sql
├── schema/070_foreignkeys.sql
├── schema/080_modules.sql           vistas, funciones, procedimientos
├── schema/085_triggers.sql
├── schema/090_finalize.sql          reseed de identidades, secuencias, SYSTEM_VERSIONING
├── data/dbo.Customer.jsonl          una fila por línea
├── data/dbo.Order.0000.jsonl        tablas grandes, un fichero por rango
└── README.txt
```

Las fases `.sql` las produce el compositor de SqlSchemaDiff.Core en orden topológico. Que
sean ejecutables a mano no es decoración: es lo que hace que el archivo sirva de archivo
cuando ya no exista la herramienta.

### El manifiesto

Un `DatabaseSnapshot` de SqlSchemaDiff.Core — el mismo objeto que el diff compara — más lo
que un restore y un verify necesitan saber por tabla:

```jsonc
{
  "formatVersion": 1,
  "tool": { "name": "SqlArchive", "version": "0.1.0" },
  "createdAt": "2026-09-07T22:30:00Z",
  "source": { "server": "SQL2022", "database": "Ventas", "edition": "...", "collation": "..." },
  "consistency": "per-table",          // o "snapshot", ver más abajo
  "schema": { /* DatabaseSnapshot completo */ },
  "tables": [
    {
      "schema": "dbo", "name": "Customer",
      "rowCount": 41203,
      "rowHash": "9f2c...",             // ver "La igualdad, definida"
      "dataFiles": ["data/dbo.Customer.jsonl"],
      "fileHashes": { "data/dbo.Customer.jsonl": "sha256:..." },
      "rowFilter": null,                // el --where con el que se exportó, si hubo
      "dataSkipped": false
    }
  ]
}
```

`rowFilter` y `dataSkipped` van en el manifiesto y no en una nota aparte porque un archivo
parcial que no dice que es parcial es peor que no tenerlo: un `verify` contra él
reportaría diferencias que no son diferencias.

---

## La igualdad, definida

Ésta es la decisión de la que cuelga todo lo demás, así que va explícita.

**El hash de una tabla es la suma módulo 2²⁵⁶ de los SHA-256 de cada línea JSONL canónica
de esa tabla.**

> **Corregido el 7 de septiembre, después de escribir esto.** Aquí decía *XOR*, y WP 2.1
> lo refutó al implementarlo. El XOR es una involución, así que dos contribuciones
> idénticas se cancelan — y el problema no es el caso evidente que el conteo de filas sí
> atrapa, sino que **`{A,A,B,B}` y `{C,C,D,D}` dan cero los dos, con cuatro filas cada
> uno**. Un restore que copió un rango dos veces y se dejó otro aterriza justo ahí, y una
> tabla sin clave primaria es justo donde viven las filas duplicadas. La suma conserva
> todas las propiedades que hacían falta y no tiene involución. El razonamiento entero
> está en `FORMAT.md`, bajo *Why the sum and not XOR*.

Tres consecuencias, y las tres son el motivo:

1. **No depende del orden.** El export lee en paralelo por rangos y `verify` puede leer en
   otro orden; la suma da el mismo resultado, y también da el mismo si se suma por
   particiones y luego se suman las particiones. Un hash que dependiera del orden
   obligaría a ordenar las dos lecturas, que en una tabla grande es el coste dominante.
2. **No depende de SQL Server.** La igualdad la define el formato del archivo, no el
   servidor: la misma fila exportada desde 2016 y desde 2022, con distinta collation en la
   conexión, produce el mismo byte a byte porque la codificación de valores es nuestra.
   Un `CHECKSUM_AGG` habría atado la respuesta a la versión y a la edición del motor.
3. **Export y verify comparten implementación.** Exportar ya lee cada fila y ya la
   codifica; el hash sale gratis. Verificar contra una base viva vuelve a leerla y a
   codificarla, que es una pasada completa de todas formas.

Detecta un `UPDATE`, que es lo que un conteo de filas no ve — mil filas modificadas siguen
siendo mil filas. **No dice qué fila cambió**, y esa es la limitación aceptada a cambio de
un manifiesto que ocupa bytes por tabla en vez de tanto como los datos. Si un día hace
falta reconciliar fila por fila, se añade un modo opcional que guarde los hashes por fila
en un fichero aparte, no en el manifiesto.

**Dos familias de columnas no viajan**, y por la misma razón las dos: el servidor las
escribe él y rechaza que se las escriban. Un `rowversion` recibe uno nuevo al restaurar, y
una columna calculada no tiene valor propio. Llevarlas haría que `verify` no pudiera pasar
nunca sobre una tabla que las tenga. El manifiesto las declara en `omittedColumns` para
que su ausencia sea un hecho registrado y no un hueco. (Las columnas `GENERATED ALWAYS` de
una tabla ledger tampoco viajan; ningún restore de este formato reconstruye un ledger.)

> **Corregido el 10 de septiembre, en WP 2.6.** Aquí decía *tres* familias: las columnas
> de período de una tabla versionada eran la tercera, porque el servidor las rechaza
> **incluso con `SYSTEM_VERSIONING = OFF`**. Eso sigue siendo cierto y la conclusión no lo
> era. Las rechaza **mientras exista el período**, y un restore puede crear la tabla sin
> él, cargar las filas con su `ValidFrom` y su `ValidTo` y añadir el período después
> — medido contra SQL Server 2025, y la razón de ser de `ComposeOptions.PeriodAfterData`
> en SqlSchemaDiff 1.8. Sin ellas cada fila restaurada empezaba en el instante del
> restore, y `FOR SYSTEM_TIME AS OF` cualquier momento anterior no devolvía nada. **Ahora
> viajan**, y con ellas la tabla de historia entera: una tabla más del archivo, con su
> entrada en el manifiesto, sus filas, su conteo y su hash. `FORMAT.md` tiene el detalle,
> bajo *System-versioned tables*.

**La tabla de historia no se deriva de su padre: se lee.** Lo que SQL Server pone en una
historia es decisión suya y no es "el padre menos algo": la identidad del padre es allí
una columna corriente, una columna calculada del padre es allí una columna real con datos,
el período son dos `datetime2` normales. Así que la misma regla de columnas, aplicada a la
historia tal como está en el catálogo, lleva justo lo que el padre omite — con una
excepción, el `rowversion`, que SQL Server conserva en la historia como `timestamp` y que
nada puede escribir: ni un `INSERT`, ni una columna `binary(8)` que la versión adopte
(13525), ni un `ALTER COLUMN` a `timestamp` (4927). Las filas de la historia se
restauran; ese valor, en ellas como en la tabla, es el del destino.

**La tabla de codificación de valores es normativa**, no un detalle de implementación: es
la definición de la igualdad. Se adopta la de dbdumper — decimales como texto, fechas ISO
con `T`, binarios en base64 — y se documenta entera en `FORMAT.md`, con una prueba por
tipo de columna que haga la ida y la vuelta byte a byte.

---

## Export

- Filtros por glob de tabla, `--where` por tabla, exclusión de datos manteniendo el
  esquema.
- Lectura paralela por tabla, y por rangos dentro de una tabla grande cuando hay una clave
  numérica o de fecha por la que partir.
- Spool a disco y **reanudación con huella**: si el export se corta, se retoma por las
  tablas que faltan. La huella incluye la cadena de conexión, los filtros y el
  `formatVersion`, para que reanudar con otros parámetros sea un error y no una mezcla.
- Hash por tabla y por fichero, calculados al vuelo.

### Consistencia

Configurable, y **por defecto tabla por tabla**, porque un punto único en el tiempo exige
permisos y espacio que muchos clientes no dan, y forzarlo haría la herramienta inservible
en el caso común.

`--consistent` intenta, en este orden:

1. **Un snapshot de base de datos** (`CREATE DATABASE ... AS SNAPSHOT OF ...`) y exportar
   de él. Conserva el paralelismo. Exige permiso de `CREATE DATABASE`, espacio, y no
   existe en Azure SQL Database.
2. Si no se puede, **una sola conexión con `SNAPSHOT` isolation**. Pierde el paralelismo y
   exige `ALLOW_SNAPSHOT_ISOLATION ON`.
3. Si tampoco, **se rechaza** diciendo cuál de las dos condiciones falta. No cae en
   silencio a tabla por tabla: alguien que pidió consistencia y recibió un archivo
   inconsistente sin enterarse está peor que si hubiera fallado.

El modo usado se escribe en el manifiesto, porque dentro de dos años nadie va a recordar
con qué se sacó.

---

## Import

Tres modos, y el tercero es la razón por la que existen la Fase 0 y la Fase 1.

**`--schema-only`** ejecuta las fases `.sql` y nada más.

**`--data-only`** salta el esquema y publica los datos en un destino que ya tiene la forma.

**Restaurar sobre una base existente es una migración con diff**, no un drop:

1. Se compara el esquema del archivo con el del destino, con el mismo diff de SQLDiff que
   ya preserva datos: `ALTER` donde se puede, rebuild que conserva filas donde no.
2. Cada tabla se publica con el ciclo de SyncJob.Core — staging desde el catálogo del
   destino, guardia, y swap por `ALTER TABLE ... SWITCH`. Atómico por tabla, y un fallo a
   mitad deja el destino como estaba.
3. Las fases posteriores a los datos —índices, checks, FKs— se aplican después, que es
   para lo que el compositor las separa.

**La guardia aquí es exacta, no heurística.** SyncJob adivina con un piso de filas y una
fracción del destino porque no sabe cuántas filas debería haber. Aquí sí: el manifiesto lo
dice. La regla es que **el número de filas y el hash de lo preparado tienen que coincidir
con lo que el manifiesto declara**, y si no coinciden no se publica esa tabla. Un archivo
truncado, un fichero corrupto o una lectura a medias se detienen antes de tocar el
destino, y la que se detiene es esa tabla y no el restore entero.

Reanudable por tabla, con la misma huella que el export.

---

## Verify

Contesta tres preguntas distintas con el mismo manifiesto:

| Pregunta | Contra qué compara |
|---|---|
| ¿El archivo está íntegro? | `fileHashes` contra los ficheros del zip. No toca ningún servidor. |
| ¿El destino restaurado coincide con el archivo? | Esquema por diff, y por tabla el conteo y el hash. |
| ¿Una base viva ha derivado del archivo? | Lo mismo, apuntando a producción. Es el uso de auditoría. |

Salida legible y JSON. El veredicto por tabla dice **qué** difiere: el esquema, el número
de filas, o el contenido con el mismo número de filas — que es el caso que un conteo no ve
y el que más importa.

`inspect` muestra el manifiesto y la lista de entradas sin descomprimir el archivo.

---

## Interoperabilidad con dbdumper

Se lee su `manifest.json` v1 como un lado de snapshot, para que `verify` y
`restore-as-migration` funcionen sobre un archivo suyo. **No** se escribe su formato: sin
`rowHash` no hay nada que verificar más allá del conteo, que es justo lo que venimos a
mejorar.

---

## Fuera de alcance en la Fase 2

- **Subconjunto coherente por FK y máscaras** — Fase 4. Los cambios de formato que
  necesitan (`rowFilter` por tabla, y declarar en el manifiesto qué columnas se
  enmascararon) ya están previstos arriba para no romper `formatVersion` 1 después.
- Bases que no sean SQL Server.
- Objetos fuera de la base: logins de servidor, jobs del Agent, linked servers. El
  manifiesto puede listarlos como notas, pero el archivo no los restaura.

---

## Huecos conocidos, reportados y no cerrados

Los encontró WP 2.2 usando el formato de WP 2.1. Ninguno bloquea nada hoy; los tres se
resuelven mejor con la cabeza fresca que con prisa.

- **`ArchiveTableEntry.RowCount` es `long` y no puede decir «no sé».** Una tabla con
  `dataSkipped` queda en 0, que se lee como «cero filas». `DataSkipped` desambigua, pero
  obliga a un lector a consultarlo *primero*. Un `long?` lo diría solo. Cambiarlo ahora es
  barato porque nada está publicado.
- **Los hashes de `schema/*.sql` difieren entre Windows y Linux**, porque
  `SqlRender.EnsureTrailingGo` usa `Environment.NewLine`. **Deliberadamente no se
  normaliza:** pasar CRLF a LF cambiaría el texto de un módulo en `sys.sql_modules` tras
  restaurar, y el diff del import lo leería como deriva. Es cosmético — el hash sólo se
  compara contra su propio archivo — pero conviene que esté escrito.
- **Para SqlSchemaDiff 1.8:** `SqlServerSchemaExtractor.ExtractAsync` sólo acepta una cadena
  de conexión, así que bajo `snapshot-isolation` el esquema se lee **fuera** de la
  transacción y no comparte instante con los datos. Bajo `snapshot` no ocurre, porque la
  base entera está congelada. Es un hueco de API del motor de esquema, no del formato.

### Lo que encontraron 2.3 y 2.4 contra el servidor

Los cinco primeros son defectos de los motores, no del formato, y ninguno está corregido
en ellos: el import los rodea y las pruebas fijan el comportamiento actual, de modo que el
día que el motor cambie lo dice una prueba y no un cliente.

- ~~**Un archivo no lleva el historial de una tabla versionada.**~~ **Cerrado por WP 2.6,
  con fidelidad completa, que es lo que Pedro eligió.** El extractor salta la tabla de
  historia —correcto para un diff— y el export la vuelve a leer por nombre con
  `ExtractTableAsync`, que sí la devuelve, y la archiva como una tabla más. El `040` crea
  la tabla sin su período y la historia como tabla corriente; se cargan las dos, período
  incluido; el `090` añade el período y enciende la versión, que adopta la historia con
  sus filas. Una tabla restaurada contesta `AS OF` como el origen en todo instante,
  incluido el hueco entre el último cambio y el restore, que antes no devolvía nada.
  Sobre un destino que ya es temporal —la migración, y `--data-only`— la versión y el
  período se quitan y se ponen en **una sola transacción** por tabla y su historia, y un
  fallo a mitad deja el destino exactamente como estaba, período, versión, `HIDDEN` y
  filas: medido, porque SQL Server deshace él mismo la transacción cuando rechaza la
  historia (13573). Tres cosas quedan, y ninguna es silenciosa:
  - **El `rowversion` de una historia no se puede restaurar**, como el de la tabla; se
    declara en `omittedColumns` y las filas llegan con el valor del destino.
  - **Un servidor cuyo reloj va por detrás del de origen** rechaza el período (13542) o la
    historia (13543) hasta que su reloj alcanza el último instante del archivo. El import
    lo dice con esas palabras; en una migración, deshace la transacción.
  - **`HISTORY_RETENTION_PERIOD` no está en el snapshot** de SqlSchemaDiff, así que un
    restore sobre una base vacía la deja infinita. Sobre un destino existente se
    conserva la que tenía.
  - **Una tabla temporal *memory-optimized* no se puede migrar sobre sí misma**: SQL
    Server rechaza apagarle la versión dentro de una transacción (12331, medido en 2025),
    y sin transacción no hay promesa que cumplir. Se rechaza esa tabla, se dice por qué y
    el destino queda como estaba. Sobre una base donde no existe, el límite es el de
    siempre: el filegroup.
- **`DBCC CHECKIDENT(t, RESEED, n)` significa dos cosas distintas.** En una tabla que ha
  recibido un `INSERT`, el siguiente valor es `n+1`; en una que no, es `n`. El destino de
  un restore es siempre del segundo tipo —lo crea la fase 040 y lo llena un `SWITCH`, que
  no es un insert— así que el reseed de `SwapPublisher` a `MAX(staged)` deja el contador
  una unidad corto y **la primera fila que alguien inserte choca con la última
  restaurada**. Medido en SQL Server 2025. El import ejecuta la forma sin valor, que no
  tiene esa ambigüedad.
- **`SwapCapability` no ve una tabla temporal con el versionado apagado**, porque lee
  `sys.tables.temporal_type`, que vale 0 en ese estado —justo el estado en el que un
  restore carga filas. La comprobación previa pasa y el `SWITCH` falla con 13577.
- **En una migración, ninguna tabla tocada por una FK se puede publicar por `SWITCH`.**
  `SwapAlignment` recrea las claves del destino sobre la staging **habilitadas y `WITH
  CHECK`**, así que la staging se valida contra un padre que está a mitad de
  reemplazarse. Ningún orden de carga lo arregla, porque todos los hijos se están
  recargando a la vez. El import apaga las claves alrededor de la fase de datos y las
  deja exactamente en el estado en que estaban.
- **`SwapPublisher.SwapAsync` lee el catálogo de la base entera por cada tabla que
  publica.** En un restore de cincuenta tablas son cincuenta lecturas completas de
  metadatos. Es la misma carencia que ya está anotada para SQLDiff 1.8: falta un punto de
  entrada por tabla en el extractor.

Y uno que no es un defecto sino un hecho del servidor, que costó la prueba decisiva de
2.4: **`ALTER SEQUENCE … RESTART WITH n` mueve `start_value`, no sólo el valor actual.**
Como el `090_finalize.sql` emite ese reinicio, todo archivo con una secuencia usada
verificaba sucio contra la base construida a partir de él. SQL Server no guarda el valor
declarado en ninguna parte, así que no hay forma de distinguir un reinicio de una
redeclaración: la propiedad sale de la comparación y el tipo, el incremento, los límites,
el ciclo y la caché se siguen comparando.

---

## Paquetes de trabajo

| | Qué | Depende de |
|---|---|---|
| **2.1** | Formato: manifiesto, fases, JSONL, codificación de valores, lector del `.dbdump` de JeePee | — |
| **2.2** | Export: filtros, rangos, reanudación, modos de consistencia, hashes | 2.1 |
| **2.3** | Import: los tres modos, la migración con diff, la guardia exacta | 2.1, 2.2 |
| **2.4** | Verify e Inspect | 2.1, 2.2 |

Los cinco están hechos. Los tres últimos se escribieron en paralelo el 9 de septiembre,
cada uno en su worktree, y mezclaron sin un solo conflicto.
| **2.5** | CLI, README, guía de bolsillo, CI con contenedor, release con trusted publishing | — |

2.1 va primero y solo: es el contrato del que dependen los otros tres. 2.2 y 2.5 pueden ir
en paralelo después.
