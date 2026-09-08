# The SqlArchive format, version 1

**This document is normative.** It defines what is in an archive and what it means for
two rows to be equal. `DESIGN.md` says why; this says what.

The shape and the value-encoding table are adopted from
[dbdumper](https://github.com/JeePeeTee/dbdumper) by JeePee (MIT), with thanks. No code
is copied. Where this document differs from dbdumper's, the difference is marked and the
reason given.

An archive is written once and read when whoever wrote it is no longer there. Everything
below is chosen for that: refuse rather than guess, write plainly rather than compactly,
and never let a value that could not be represented pass as one that was.

---

## The container

A zip. The conventional extension is `.sqlarchive`.

```
Ventas.sqlarchive
├── manifest.json                    the schema, and per table the row count and hash
├── schema/010_schemas.sql           the phases, in the order of their numeric prefixes
├── schema/020_types.sql
├── schema/030_sequences.sql
├── schema/035_synonyms.sql
├── schema/040_tables.sql
├── schema/050_indexes.sql
├── schema/060_checks.sql
├── schema/070_foreignkeys.sql
├── schema/080_modules.sql
├── schema/085_triggers.sql
├── schema/090_finalize.sql
├── data/dbo.Customer.jsonl          one table's rows
├── data/dbo.Order.0000.jsonl        a table read in ranges, one entry per range
├── data/dbo.Order.0001.jsonl
└── README.txt
```

`manifest.json` is written last, because it carries the hashes of everything else. That
costs nothing: a zip keeps its directory at the end, so `inspect` reads the manifest and
lists the entries without decompressing a byte of data.

### Entry names

Schema phases take their file names from the composer, which prefixes them so that a
lexicographic sort is the execution order. Sort them **ordinally**, not by culture.

A table's rows go to `data/{schema}.{table}.jsonl`, or
`data/{schema}.{table}.{NNNN}.jsonl` when the table was read in ranges — four digits, so
a listing sorts into range order.

Both parts of the name are percent-encoded. `%`, `.`, `/`, `\`, `:`, `*`, `?`, `"`, `<`,
`>`, `|`, every character below U+0020, U+007F, and a trailing space become `%XX` with
uppercase hex over the character's UTF-8 bytes. Everything else, letters and digits and
spaces and every non-ASCII character, is left alone.

The dot is escaped along with the rest because the dot is the separator: without that,
`[dbo].[My.Table]` and `[dbo.My].[Table]` would produce the same entry name. The encoding
is injective, so two identifiers can never collide, and it is never reversed — the
manifest lists each table's entries explicitly, so nothing has to parse a name back into
an identifier.

Two tables whose names differ only in case are distinct under a case-sensitive collation,
and produce distinct entries that a zip stores happily. Extracting both onto a
case-insensitive filesystem would collide; reading them out of the archive, which is what
SqlArchive does, does not.

---

## The data files

One row per line. UTF-8, no byte-order mark, lines terminated with a single `\n` on every
platform — a writer that let the environment choose would give the same rows different
hashes on Windows and Linux, which is the one thing the hash exists to rule out. A reader
tolerates `\r\n` and strips the carriage return before hashing, and tolerates a final
line with no terminator.

A row is a JSON **object** on one line: no whitespace anywhere, properties in the table's
`column_id` order, keys being the column names.

```jsonl
{"Id":1,"Name":"Acme","Balance":"1250.0000","CreatedUtc":"2026-08-27T09:14:22.1234567","Photo":null}
{"Id":2,"Name":"Böhm & Co","Balance":"-3.5000","CreatedUtc":"2026-08-27T09:14:23","Photo":"iVBORw0KGgo="}
```

> **Differs from dbdumper.** dbdumper writes a header line naming the table and its
> columns, then one JSON *array* per row. An object per line costs more bytes and is
> worth it here: every line stands on its own, so a `grep` for a customer id returns
> something a person can read, and a file that lost its first line is still a file rather
> than a puzzle.

### Which columns are carried

Every column of the table in `column_id` order, **except** three kinds, which are left
out because SQL Server refuses to be told what they are:

| Left out | Why |
|---|---|
| Computed columns | Derived from the others. `INSERT` refuses to name one; its value in a restored table follows from the values that were restored. |
| `rowversion` / `timestamp` | Assigned by the server from a counter belonging to one database. `INSERT` refuses: *"Cannot insert an explicit value into a timestamp column."* Its value means nothing in another database, and a restored row necessarily gets a different one. |
| `GENERATED ALWAYS AS ROW START` / `ROW END` | The period columns of a system-versioned table. Refused **even with `SYSTEM_VERSIONING` set to `OFF`**, which is the state a restore loads rows in — verified against SQL Server 2025. |

> **Differs from the work-package spec**, which lists `rowversion` in the encoding table.
> Its encoding is defined below and is reachable if a caller asks for it, but the format
> does not carry the column: an archive that included it could never pass its own
> `verify` after a restore, because the destination's values are necessarily different.

The manifest records which columns a table omitted and why, and so does the README, so a
person reading the JSONL and counting fewer columns than the `CREATE TABLE` has an answer
without reading any source.

---

## The value-encoding table

**This is the definition of equality.** Two rows are the same row when these bytes are
the same. It is not an implementation detail of the exporter.

| SQL type | JSON | Notes |
|---|---|---|
| any type, `NULL` | `null` | Distinct from `""`, from `0` and from `false`. |
| `bit` | `true` / `false` | A two-valued type written as JSON's two-valued type. `1`/`0` would leave a reader unable to tell a `bit` from an `int` without the schema, and the archive should say what the value *is*. Same as dbdumper. |
| `tinyint`, `smallint`, `int`, `bigint` | JSON number, no exponent | A `bigint` past 2^53 is exact here and would lose precision in a consumer that parses every JSON number into a double. Ours does not; a hand-written one should use a 64-bit integer parse. |
| `decimal`, `numeric` | JSON **string** | Read through `SqlDecimal`, so all 38 digits of precision survive — `GetValue` on a `decimal(38,10)` at its limit throws `OverflowException: Conversion overflows`, because .NET's `decimal` holds 28. Written with exactly the scale the column declares and no exponent: a `decimal(19,4)` holding one is `"1.0000"`. Those zeros are not padding, they are what the type says the value has. |
| `money`, `smallmoney` | JSON **string**, four decimals | Read through `SqlMoney.Value`, never `SqlMoney.ToString()`, which writes a *variable* number of decimals — at least two, with anything beyond them trimmed — so one column would archive `1` as `"1.00"` and `1.0001` as `"1.0001"`, at two different scales. |
| `float` | JSON number | Shortest round-trippable at double precision — on .NET this is both the default format and `R`. Exponential notation appears at the extremes (`1.7976931348623157E+308`) and is legal JSON. |
| `real` | JSON number | Shortest round-trippable **at single precision**. A `real` holding 0.1 is `0.1`; the same bits formatted as a double would be `0.10000000149011612`, which round-trips and is not the value. |
| `date` | `"2026-01-15"` | |
| `time` | `"10:00:00.1234567"` | The fraction and its point appear only where there is one. `TimeSpan`'s `FFFFFFF` specifier leaves the point behind on a whole second, so a whole-second time uses a separate format — `"10:00:00."` is neither ISO 8601 nor accepted by SQL Server. |
| `datetime`, `smalldatetime`, `datetime2(0..7)` | `"2026-01-15T10:00:00.003"` | ISO 8601 with the `T`. The space-separated form is read according to the session's `DATEFORMAT`; the `T` form is read the same way under every language setting. Trailing zeros in the fraction are trimmed, and with them the point. |
| `datetimeoffset` | `"2026-01-15T10:00:00.1234567+02:00"` | The offset is always written numerically, `+00:00` included. |
| `char`, `varchar`, `nchar`, `nvarchar`, `text`, `ntext`, `xml` | JSON string | See "Strings" below. |
| `uniqueidentifier` | `"3f2504e0-4f89-11d3-9a0c-0305e82c3301"` | The `D` form, **lower case**. Either would do and one had to be picked for the bytes to be stable; lower case matches every other hex string this format writes. SQL Server displays them upper case and reads either. |
| `binary`, `varbinary`, `image` | base64 | Standard alphabet, padded. A `binary(n)` is padded with zeros by the server and all `n` bytes are encoded. |
| `timestamp` / `rowversion` | base64 | Defined, but not carried — see above. |
| `hierarchyid`, `geography`, `geometry` | base64 | The server's own serialisation, read with `GetBytes` and written back through a `varbinary` parameter. Neither direction needs `Microsoft.SqlServer.Types`, which means an archive with geography in it can be made and restored on Linux. Verified against a live server for all three. Same as dbdumper. |
| `sql_variant` | — | **Not supported in version 1.** Refused, naming the column. Every other type says what it is in the schema; a `sql_variant` says it per value, and its base type, precision and collation would have to travel with each one for a restore not to guess. |
| anything else | — | **Refused, naming the type.** dbdumper falls back to a string representation. That fallback is what turns an unknown type into an archive that parses, verifies against itself and restores something that was never there. |

### Strings

Escaped by rules this format owns rather than by a JSON library's policy, because a
library is free to change its escaping between versions and the day it does, every hash
written before it stops matching the same rows read after it.

Escaped: `"` as `\"`, `\` as `\\`, backspace, form feed, newline, carriage return and tab
as their short forms, and every other character below U+0020 plus U+007F as `\u00xx` with
**lower case** hex.

Not escaped: everything else. `<`, `>` and `&` go through raw. So do U+2028 and U+2029,
which are legal in a JSON string and only trouble a JavaScript `eval`. So does every
non-ASCII character, as raw UTF-8 — `Böhm`, `日本語` and `😀` appear as themselves, which
is the point of a readable archive.

**Unpaired UTF-16 surrogates are refused, not substituted.** SQL Server accepts one in an
`nvarchar` — `NCHAR(0xD800)` stores and `UNICODE()` reads it back — and it has no UTF-8
representation. In practice this is unreachable through the driver:
`Microsoft.Data.SqlClient` decodes `nvarchar` with UTF-16 replacement, so the value
arrives here as U+FFFD and that is what gets archived. **The loss happens in the driver,
before this format sees anything**, and the live tests assert exactly that so the day a
driver stops sanitising, we find out. If one does reach the encoder, it throws: every
library's answer is to write U+FFFD quietly, and that substitution would be ours, in our
bytes, in the definition of equality.

---

## The hash

**A table's hash is the sum, modulo 2^256, of the SHA-256 of each of its canonical JSONL
lines** — the line exactly as it is in the file, *without* its terminator. Lowercase hex,
64 characters, no prefix.

The terminator is excluded so that a row written as the last line of a range file and the
same row written in the middle of a whole-table file hash identically. Otherwise
re-reading a table with different range boundaries would report drift that is not there.

Three properties, and they are the reasons:

1. **Order independence.** Export reads a large table in parallel ranges and `verify` may
   read it back in another order or with other boundaries. A hash that depended on the
   order would force both sides to sort, which on a large table is the dominant cost.
2. **Independence from SQL Server.** The same row exported from 2016 and from 2022,
   through connections with different collations, gives the same bytes because the
   encoding is ours. `CHECKSUM_AGG` would have tied the answer to the engine's version
   and edition.
3. **Export and verify share one implementation.** Exporting already encodes every row,
   so the hash is one pass over bytes in cache. Verifying against a live database reads
   and encodes again, which is a full pass either way.

It sees an `UPDATE`, which a row count cannot — a thousand modified rows are still a
thousand rows. It does **not** say *which* row changed. That is the accepted trade for a
manifest that costs bytes per table instead of as much as the data.

### Why the sum and not XOR

`DESIGN.md` says XOR. XOR is an involution, so identical contributions cancel, and the
work package asks whether that matters and whether the row count beside it resolves it.
It does not.

The obvious case — a table whose rows are each duplicated an even number of times hashes
the same as an empty one — *is* caught by the row count. But it is a symptom. Under XOR,
**any** two multisets whose difference cancels in pairs collide: `{A,A,B,B}` and
`{C,C,D,D}` both hash to zero **and both have four rows**, so the count catches neither.
A restore that copied one range twice and dropped another is exactly how you arrive
there, and a table without a primary key — a heap, a log, a staging table — is exactly
where duplicate rows live.

Addition modulo 2^256 keeps every property `DESIGN.md` asked for. It is commutative and
associative, so order and partitioning still do not matter; it is a group, so a
contribution can still be removed if an incremental mode ever wants that; it costs the
same. And duplicates no longer cancel. It is also the standard construction for hashing a
multiset rather than a set — the additive form is the one with a security argument behind
it, and the XOR form is the one that needs a nonce and set semantics to have any.

An empty table is 64 zeros. The row count sits beside the hash in the manifest and is
compared first; the two together are the check.

### File hashes

Plain SHA-256 of each entry's bytes, hex, prefixed `sha256:`. The prefix is what tells
them apart from a row hash, which carries none because it is not the SHA-256 of anything.

Between the two, "is this archive intact?" is answerable with no database anywhere near
it.

---

## The manifest

```jsonc
{
  "format": "sqlarchive",
  "formatVersion": 1,
  "tool": { "name": "SqlArchive", "version": "0.1.0" },
  "createdAt": "2026-09-07T22:30:00+00:00",
  "source": {
    "server": "SQL2022",
    "database": "Ventas",
    "edition": "Developer Edition (64-bit)",
    "productVersion": "16.0.4165.4",
    "collation": "SQL_Latin1_General_CP1_CI_AS"
  },
  "consistency": "per-table",
  "schema": { /* a whole SqlSchemaDiff DatabaseSnapshot */ },
  "tables": [
    {
      "schema": "dbo",
      "name": "Customer",
      "rowCount": 41203,
      "rowHash": "9f2c...",
      "dataFiles": ["data/dbo.Customer.jsonl"],
      "fileHashes": { "data/dbo.Customer.jsonl": "sha256:..." },
      "rowFilter": null,
      "dataSkipped": false,
      "omittedColumns": { "Version": "rowversion" }
    }
  ],
  "files": {
    "schema/040_tables.sql": "sha256:...",
    "README.txt": "sha256:..."
  }
}
```

`schema` is a whole `DatabaseSnapshot` — the very object SqlSchemaDiff's differ compares.
That is what makes `restore-as-migration` and `verify` possible with no translation layer:
one side of the diff comes out of a file and the other out of a server.

`consistency` is `per-table`, `snapshot` or `snapshot-isolation`. It is written down
because in two years nobody will remember which flags the export ran with, and the answer
changes what a difference between two tables means.

`rowFilter` and `dataSkipped` are present from version 1 even though Phase 4 is what will
fill them: a partial archive that does not say it is partial makes `verify` report
differences that are not differences.

`files` covers every entry that is not a table's data and not the manifest itself — the
schema phases and the README. Together with each table's `fileHashes` it covers the whole
archive exactly once, with nothing recorded twice and nothing left out.

Nulls are written rather than omitted. `"rowFilter": null` says the field exists and this
table was not filtered; leaving it out leaves a reader to work out whether the export had
no filter or the writer had no such concept.

> **Additions to the sketch in `DESIGN.md`:** `format`, `source.productVersion`,
> `omittedColumns` and the top-level `files`. `format` is the one that matters — dbdumper's
> manifest also declares `formatVersion: 1`, and without something naming whose format it
> is, telling the two apart comes down to which properties happen to bind.

### Reading a version you do not know

A manifest declaring a `formatVersion` higher than the reader understands is **not** an
error to parse. The whole point of an archive is to be opened by whoever is there when
the source is not, and *"this is version 3, I read version 1, here is what I can still
tell you"* beats a parse failure. Unknown properties are kept rather than dropped, at the
root and per table, so `inspect` can show them and a rewrite does not throw them away.
Refusing happens where it matters — at the point of restoring data — and the manifest
carries a flag saying so.

---

## Reading a dbdumper archive

dbdumper's `manifest.json` version 1 is read and mapped onto a `DatabaseSnapshot`, so
`verify` and `restore-as-migration` work on an archive somebody else made.

Its `manifest.json` carries no per-table row hash, so the table entries come back with
none, and a verify against one compares the schema and the row counts and stops there.
That is stated rather than papered over: filling the field with something computed from
the data files would make an unverifiable archive look verified.

**SqlArchive does not write dbdumper's format.** Without a row hash there is nothing to
verify beyond a row count, and improving on the row count is the reason this tool exists.
