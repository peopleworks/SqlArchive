# Changelog

All notable changes to SqlArchive are recorded here.
This project follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until 1.0 the public API of
`PeopleWorks.SqlArchive.Core` may still change between minor versions; the archive format is
versioned separately, by `formatVersion` in every manifest.

## [0.1.0] - 2026-09-11

The first release. Four verbs, one archive format, and nothing of its own underneath: the schema
side is `PeopleWorks.SqlSchemaDiff.Core` 1.8.1 and the data side is `PeopleWorks.SyncJob.Core`
1.0.0, referenced as packages and never copied.

### Added

- **The archive format**, `formatVersion` 1: a zip holding a manifest, the schema as numbered
  phases from `010_schemas.sql` to `090_finalize.sql` that run by hand without the tool, and one
  JSONL file per table — or per range of a large one. The manifest carries a full schema snapshot
  and, per table, the row count and a content hash. [`FORMAT.md`](FORMAT.md) is the normative
  specification, and its value-encoding table is literally the definition of two databases being
  equal.
- **The row hash is the sum modulo 2²⁵⁶ of the SHA-256 of each canonical line**, not the XOR the
  design first said. XOR is an involution: `{A,A,B,B}` and `{C,C,D,D}` both hash to zero with four
  rows each, and a restore that copied one range twice and dropped another lands exactly there.
  The sum keeps every property that mattered — order-independent, so ranges read in parallel;
  server-independent, so the same row from 2016 and from 2022 hashes the same — and has no
  involution.
- **`export`**: table globs, `--where` per table, schema-only tables, parallel reads split into
  ranges on an indexed, non-nullable column, a resumable spool whose fingerprint refuses a resume
  with different parameters, and three consistency modes — per table by default, a database
  snapshot, or one connection under `SNAPSHOT` isolation — where asking for consistency and not
  getting it is an error, never a silent fallback.
- **`import`**: an empty destination gets the archive's own phases around the data; one that
  already holds tables gets a schema diff first, as a migration rather than a drop. Each table is
  published through staging and a switch, so a failure half way leaves it as it was. The guard is
  exact rather than heuristic: what came out of the archive and what landed in staging must both
  match the manifest's count and hash, or that table is not published. Resumable by table.
- **`verify`**: is the archive intact — with no server anywhere near it; does a restored database
  match it; has a live one drifted. The verdict names the table and says whether it differs by
  schema, by row count, or by content at the same row count — the case a count cannot see. Exit
  code **3** for "the comparison ran and found differences", distinct from **1** for "it could not
  make the comparison".
- **`inspect`**: the manifest and the entry list, without unpacking a byte — on our archives and on
  [dbdumper](https://github.com/JeePeeTee/dbdumper)'s, whose shape and value encoding this format
  adopts, with credit and without its code.
- **System-versioned tables keep their timeline.** The history travels as a table of its own and
  the period columns as data; the period is put back on the rows after they are loaded, the only
  order SQL Server accepts that keeps them. A restored temporal table answers
  `FOR SYSTEM_TIME AS OF` as the source did at every instant — including the stretch between the
  last change and the restore, where a naive copy answers nothing.

### Found against a real server, and designed around

Every one of these was found by a live test failing, not by reading code, and each is written
down where it applies.

- `ALTER SEQUENCE … RESTART WITH n` moves `start_value` as well as the current value, so a
  restored sequence cannot be compared on how it was declared.
- `DBCC CHECKIDENT(t, RESEED, n)` gives `n` on a table that has never had an `INSERT` and `n+1` on
  one that has. A restore's destination is always the first kind; reseeding it to the maximum
  would make the first new row collide with the last restored one.
- `BeginTransaction()` with no argument sets `READ COMMITTED` explicitly, and a snapshot's instant
  is fixed at the first read of *user* data, not at `BEGIN`.
- `decimal(38,10)` at its limit makes `GetValue` throw, because .NET's `decimal` holds 28 digits.
- A partition column must be non-nullable: a nullable key drops its null rows out of every range
  at once, and the total still looks plausible.
- Tables publishing in parallel could deadlock inside SyncJob.Core's whole-catalog read. A
  deadlock victim — error 1205, and only that — is rerun a bounded number of times, which is safe
  because every publication is one transaction.

### Known limits

In the [README](README.md#what-it-does-not-carry-and-says-so-here-rather-than-letting-you-find-out):
a memory-optimized table cannot be restored into a fresh database; `--table` on `import` selects
rows, not schema; a restored identity continues from the highest current id rather than the
source's counter; and the verdict names the table that changed, never the row.

[0.1.0]: https://github.com/peopleworks/SqlArchive/releases/tag/v0.1.0
