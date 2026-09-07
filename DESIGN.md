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

**El hash de una tabla es el XOR de los SHA-256 de cada línea JSONL canónica de esa tabla.**

Tres consecuencias, y las tres son el motivo:

1. **No depende del orden.** El export lee en paralelo por rangos y `verify` puede leer en
   otro orden; el XOR da el mismo resultado. Un hash que dependiera del orden obligaría a
   ordenar las dos lecturas, que en una tabla grande es el coste dominante.
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

## Paquetes de trabajo

| | Qué | Depende de |
|---|---|---|
| **2.1** | Formato: manifiesto, fases, JSONL, codificación de valores, lector del `.dbdump` de JeePee | — |
| **2.2** | Export: filtros, rangos, reanudación, modos de consistencia, hashes | 2.1 |
| **2.3** | Import: los tres modos, la migración con diff, la guardia exacta | 2.1, 2.2 |
| **2.4** | Verify e Inspect | 2.1, 2.2 |
| **2.5** | CLI, README, guía de bolsillo, CI con contenedor, release con trusted publishing | — |

2.1 va primero y solo: es el contrato del que dependen los otros tres. 2.2 y 2.5 pueden ir
en paralelo después.
