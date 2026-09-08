# WP 2.1 — el formato del archivo

**Worktree:** `C:\Proyecto\SqlArchive\wt\wp21` (rama `wp21-format`). Commits locales; no
empujes, no mezcles.

**Lee `DESIGN.md` en la raíz del repositorio antes que nada.** Es la decisión tomada, no
una propuesta. La sección **"La igualdad, definida"** es la parte de la que cuelga todo lo
demás y este paquete es quien la implementa.

---

## Por qué este paquete va primero y solo

Los otros tres (export, import, verify) dependen de lo que escribas aquí. Si el
manifiesto o la codificación cambian después, cambian los tres. Por eso este es el único
paquete de la Fase 2 que corre sin nadie en paralelo.

Y hay una asimetría que conviene tener presente: **un fallo aquí no se descubre hasta
años después.** Un archivo se escribe una vez y se lee cuando ya no está quien lo escribió.
Una codificación que pierde precisión en un `decimal(38,10)`, o que redondea un
`datetime2(7)`, produce un archivo que parece correcto y no lo es, y `verify` dirá que
todo coincide porque compara lo que escribió consigo mismo.

## Propiedad

Tuyo, y sólo tuyo:

- `src/SqlArchive.Core/Format/` (nuevo) — todo el paquete.
- `tests/SqlArchive.Core.Tests/Format*.cs` (nuevos).
- `tests/SqlArchive.IntegrationTests/` — un fichero nuevo tuyo para la ida y vuelta contra
  un servidor real.
- `FORMAT.md` en la raíz (nuevo).

No toques `DESIGN.md`, `README.md`, los `.csproj` ni `CompositionTests.cs`.

## Hechos del repositorio

- `PeopleWorks.SqlSchemaDiff.Core` 1.7.0 trae `DatabaseSnapshot`,
  `SnapshotSerializer` (guardar y cargar, con versión de formato y tolerancia a
  propiedades desconocidas) y `SqlServerSchemaExtractor.ExtractAsync`. **El manifiesto
  lleva un `DatabaseSnapshot` entero**; no inventes un modelo de esquema.
- `PeopleWorks.SyncJob.Core` 1.0.0 no hace falta en este paquete. Es de 2.2 y 2.3.
- `CompositionTests.cs` ya comprueba que las dos referencias llegan por paquete.
- Cadena para pruebas vivas: variable `SQLARCHIVE_TEST_CONN`. Si no está, la prueba se
  salta con la razón impresa — copia el patrón de `LiveFactAttribute` de SyncJob
  (`C:\Proyecto\SOS\SyncJob\SyncJob\tests\SyncJob.IntegrationTests\LiveFactAttribute.cs`)
  y el fixture que crea y borra sus propias bases (`SqlServerFixture.cs`, mismo sitio).
  Léelos: resuelven cosas que no son obvias, como que contar filas de una tabla que no
  existe necesita dos viajes porque un lote que nombra una tabla ausente no compila
  entero.

## Alcance, en orden de prioridad

### 1. La codificación de valores — `FORMAT.md` y su implementación

**Esto es la definición de la igualdad, no un detalle.** Se adopta la de dbdumper
(decimales como texto, fechas ISO con `T`, binarios en base64) y se documenta entera.

Una fila es un objeto JSON en una línea, con las claves en el orden de `column_id` de la
tabla. Canónica quiere decir que **la misma fila produce los mismos bytes siempre**: sin
espacios, sin reordenar claves, sin variación por locale ni por cultura.

Decide y documenta, con una prueba por cada uno, al menos:

- `decimal`/`numeric`/`money` — texto, sin notación exponencial, sin ceros de más y sin
  perder ninguno de los que el tipo declara.
- `float`/`real` — el caso incómodo. `R` o `G17` conservan la ida y vuelta; elige y di por
  qué.
- `datetime`, `datetime2(0..7)`, `date`, `time`, `datetimeoffset`, `smalldatetime` — ISO
  8601. Ojo: SyncJob descubrió contra un servidor real que **`datetime` rechaza cualquier
  texto con siete decimales de segundo, aunque sean ceros**, así que la fracción se
  recorta a los dígitos que el valor tiene. Aquí escribes a un fichero y no a SQL, pero el
  import va a leerlo y a mandarlo de vuelta.
- `binary`, `varbinary`, `rowversion`, `image` — base64.
- `uniqueidentifier` — la forma `D`, en minúsculas o mayúsculas: elige.
- `bit` — `true`/`false` de JSON, o `1`/`0`: elige y di por qué.
- `nvarchar`/`varchar`/`text` — escapado JSON. Piensa en sustitutos UTF-16 sueltos, que
  SQL Server acepta y JSON no puede representar; decide qué haces y dilo.
- `xml`, `sql_variant`, `hierarchyid`, `geography`, `geometry` — los cuatro últimos son
  tipos que el driver no puede materializar sin ensamblados; documenta qué pasa con ellos
  aunque la respuesta sea "no soportado en `formatVersion` 1".
- `NULL` — distinto de la cadena vacía, obviamente, pero escríbelo en la tabla.

### 2. El hash

```csharp
public static class RowHash
{
    /// El SHA-256 de una línea JSONL canónica.
    public static byte[] OfRow(ReadOnlySpan<byte> canonicalJsonLine);

    /// Acumula sin depender del orden. Ver DESIGN.md.
    public sealed class Accumulator
    {
        public void Add(ReadOnlySpan<byte> rowHash);
        public long Rows { get; }
        public string Value { get; }   // hex en minúsculas
    }
}
```

XOR, porque el export lee en paralelo por rangos y `verify` puede leer en otro orden.
Comprueba esa propiedad con una prueba que meta las mismas filas en dos órdenes distintos
y en dos particiones distintas, y obtenga el mismo valor.

**Piensa en el caso degenerado y decide:** el XOR de dos filas idénticas es cero. Una
tabla con las filas duplicadas un número par de veces da el mismo hash que una vacía.
¿Importa? ¿Lo resuelve el `rowCount` que va al lado? Documenta la respuesta; no la dejes
implícita.

### 3. El manifiesto

Léelo en `DESIGN.md`. Modelo, escritura y lectura, con `formatVersion` y tolerancia a
propiedades desconocidas — un archivo de mañana tiene que poder abrirse hoy lo suficiente
para decir qué le falta a esta versión.

`rowFilter` y `dataSkipped` van desde ya aunque los llene la Fase 4: un archivo parcial
que no dice que es parcial hace que `verify` reporte diferencias que no lo son.

### 4. El contenedor

Escribir y leer el zip: manifiesto, `schema/*.sql`, `data/*.jsonl`, `README.txt`. Lectura
en flujo, sin descomprimir a memoria — una tabla puede ser mayor que la RAM, que es
exactamente la razón por la que SyncJob.Core existe.

`inspect` necesita leer el manifiesto y la lista de entradas **sin** descomprimir el
resto; asegúrate de que la API lo permite.

### 5. El lector de dbdumper

Leer el `manifest.json` v1 de dbdumper como un lado de snapshot. No escribas su formato.
Su repositorio es público: `github.com/JeePeeTee/dbdumper`. Si no puedes consultarlo desde
aquí, dilo en el informe y deja la interfaz definida con lo que se sabe de `DESIGN.md`, en
vez de inventarte campos.

## Pruebas

Las unitarias son casi todo esto, porque casi todo es texto puro.

**La prueba que importa** es la ida y vuelta contra un servidor real: una tabla con **una
columna de cada tipo de la tabla de codificación**, con valores en los límites — el
`decimal` más largo que el tipo admite, un `datetime2(7)` con los siete dígitos, un
`datetime` que no puede tenerlos, `NULL`, cadena vacía, un `nvarchar` con emoji, un
`varbinary` de un byte y otro de cien mil. Se lee, se escribe, se vuelve a leer, y los
bytes coinciden.

Prueba cada una rompiendo lo que cubre y viéndola fallar. Si no falla, la prueba está mal,
no el código.

## Reglas

- `TreatWarningsAsErrors` está puesto. Cero avisos.
- **Cada `await` lleva `.ConfigureAwait(false)`.** Es una librería.
- Comentarios de documentación para el porqué, no el qué. `if(` sin espacio.
- **Esta especificación puede estar equivocada.** Todos los agentes de las fases 0 y 1
  corrigieron la suya y todos tenían razón. Si algo no sobrevive al contacto con SQL
  Server, arréglalo y dilo en el informe, arriba y en negrita.

## Entrega

Commits locales en `wp21-format`. Trailers:

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_016KaXQ8tRWaEJ3fpfwuwGSM
```

Informe: qué decidiste en cada fila de la tabla de codificación y por qué; qué hiciste con
el XOR de filas duplicadas; qué pruebas comprobaste rompiendo y qué mensaje diste; los
conteos; y todo lo que encuentres que pertenezca a otro paquete.
