<div align="center">

# 🗃️ SqlArchive

**Export a SQL Server database to a readable archive. Restore it somewhere else. Prove they match.**

[![CI](https://github.com/peopleworks/SqlArchive/actions/workflows/ci.yml/badge.svg)](https://github.com/peopleworks/SqlArchive/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![SQL Server](https://img.shields.io/badge/SQL%20Server-2016%2B-CC2927?style=flat-square&logo=microsoftsqlserver&logoColor=white)](https://www.microsoft.com/sql-server)
[![Status](https://img.shields.io/badge/status-half%20built-B45309?style=flat-square)](#what-works-today)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![PeopleWorks](https://img.shields.io/badge/by-PeopleWorks-636f61?style=flat-square)](https://mvp.microsoft.com/en-US/mvp/profile/24060a02-dbc6-44ec-bca5-c213ff9835c5)

<!-- After the first release, add the two badges the sibling repositories carry. They
     are left out until then on purpose: a NuGet badge for a package nobody has
     published renders as "not found", which is not a good first impression and is not
     something a reader should have to interpret.

[![Latest release](https://img.shields.io/github/v/release/peopleworks/SqlArchive?label=release&logo=github)](https://github.com/peopleworks/SqlArchive/releases/latest)
[![NuGet](https://img.shields.io/nuget/v/PeopleWorks.SqlArchive.Cli?logo=nuget)](https://www.nuget.org/packages/PeopleWorks.SqlArchive.Cli)
-->

**[📖 Pocket guide — the format and the commands on one page](https://peopleworks.github.io/SqlArchive/)**

<!-- The guide is `docs/index.html`. It goes live once GitHub Pages is switched on for
     this repository with the source set to the `docs` folder on `main`. -->

<img src="assets/hero.svg" width="900"
     alt="Diagram of SqlArchive. A SQL Server database is exported into an archive: a zip holding manifest.json, numbered schema phases from 010_schemas.sql to 090_finalize.sql, and one JSONL file per table. The manifest carries a full schema snapshot and, per table, the row count and a content hash. Import restores it over a database that already exists, as a schema diff plus staging and a table switch. Verify answers three questions from the same manifest: is the archive intact, does a restored database match it, has a live database drifted. At the bottom, a thousand updated rows: the row count still says one thousand on both sides and sees nothing, while the row hash differs and says the content changed.">

<sub>A thousand modified rows are still a thousand rows. The per-table hash is what sees them.</sub>

</div>

---

## What works today

SqlArchive is **half built**, and this table is the honest state of it. The verbs that
are not built do not pretend: they are listed in `--help` with the options they will
take, and running one prints which work package brings it and exits non-zero.

| Verb | Today |
|---|---|
| **`inspect`** | **Works.** Reads the manifest and the entry list without unpacking a byte, on our archives and on [dbdumper](https://github.com/JeePeeTee/dbdumper)'s. |
| `export` | **Not built.** Refuses with exit code 2. Work package 2.2, being written now. |
| `import` | **Not built.** Refuses with exit code 2. Work package 2.3. |
| `verify` | **Not built.** Refuses with exit code 2. Work package 2.4. |

What *is* finished is the part everything else hangs off: **the format**, in
[`PeopleWorks.SqlArchive.Core`](src/SqlArchive.Core) — the manifest, the phased schema,
the JSONL encoding, the value-encoding table that defines equality, the row hash, and
the reader for dbdumper's manifest. [`FORMAT.md`](FORMAT.md) is its normative
specification and [`DESIGN.md`](DESIGN.md) says why each decision went the way it did.

> A command that accepts arguments and returns 0 without doing anything is worse than a
> command that is not there: a script calls it, a scheduled job reports success, and
> nobody finds out until the day somebody needs the archive.

---

## What it is

The fourth tool in the PeopleWorks database family.
[SQLDiff](https://github.com/peopleworks/SqlSchemaDiff) moves schema and
[SyncJob](https://github.com/peopleworks/syncjob) moves data; SqlArchive moves a whole
database, through a file you can read, compare and verify.

It carries no engine of its own. It composes two published packages:

| Package | What it brings |
|---|---|
| [`PeopleWorks.SqlSchemaDiff.Core`](https://www.nuget.org/packages/PeopleWorks.SqlSchemaDiff.Core) | Extract, compare and compose schema. Data-preserving `ALTER`. |
| [`PeopleWorks.SyncJob.Core`](https://www.nuget.org/packages/PeopleWorks.SyncJob.Core) | Streaming copy, staging, the row guard, publication by swap. |

## What it does that a `.bacpac` does not

- **`verify` is a real diff, not a row count.** The archive carries a full schema snapshot
  and a per-table content hash, so it sees an `UPDATE` — a thousand modified rows are
  still a thousand rows.
- **Restoring over an existing database is a migration.** The schema is diffed and altered
  in place where that preserves data; each table is published through staging with a
  guard. No dropping the database first.
- **The guard knows what to expect.** The manifest says how many rows and which hash, so a
  truncated or corrupt archive is refused before the destination is touched.
- **The archive reads without the tool.** Phased, executable `.sql` and one JSONL file per
  table. That matters most for the case where the tool is gone and the archive is not.

## The archive

A zip, conventionally named `.sqlarchive`:

```
Ventas.sqlarchive
├── manifest.json                    the schema snapshot, and per table the count and the hash
├── schema/010_schemas.sql           the phases, in the order of their numeric prefixes
├── schema/040_tables.sql
├── schema/070_foreignkeys.sql
├── schema/090_finalize.sql
├── data/dbo.Customer.jsonl          one row per line
├── data/dbo.Order.0000.jsonl        a large table read in ranges, one file per range
└── README.txt
```

The manifest is written last because it carries the hashes of everything else — and that
costs nothing, because a zip keeps its directory at the end. `inspect` reads the manifest
and lists the entries of a hundred-gigabyte archive in one seek.

**A table's hash is the sum, modulo 2²⁵⁶, of the SHA-256 of each of its canonical JSONL
lines.** Order-independent, so ranges read in parallel add up the same however they are
split; independent of SQL Server, so the same row from a 2016 and a 2022 server hashes
the same. [`FORMAT.md`](FORMAT.md) has the whole value-encoding table, which is what
actually defines two rows being equal.

---

## Install

**As a .NET tool:**

```bash
dotnet tool install -g PeopleWorks.SqlArchive.Cli
sqlarchive --version
```

**Or build from source** — .NET 9 SDK, nothing else:

```bash
git clone https://github.com/peopleworks/SqlArchive.git
cd SqlArchive
dotnet build SqlArchive.sln -c Release
dotnet run --project src/SqlArchive.Cli -- inspect Ventas.sqlarchive
```

A single self-contained `sqlarchive.exe` for Windows is attached to each release, so
`inspect` runs with no runtime to install.

> Neither the tool nor the release exists on nuget.org yet. Until the first tag,
> building from source is the only way in.

---

## `inspect`

The one verb that is finished. It reads what an archive says about itself and **never
unpacks the data**, so it is as fast on a hundred gigabytes as on a megabyte.

```bash
sqlarchive inspect Ventas.sqlarchive             # what is in this file?
sqlarchive inspect Ventas.sqlarchive --entries   # every entry, packed and unpacked
sqlarchive inspect Ventas.sqlarchive --json      # the manifest, exactly as the archive holds it
```

```
── Ventas.sqlarchive ───────────────────────────────────────────────────────────

╭─this archive is partial──────────────────────────────────────────────────────╮
│ Some of what it describes it does not contain: 1 table carrying a row filter │
│ and 1 table archived with no rows at all. The manifest records both, so a    │
│ verify knows the missing rows are absent on purpose and does not report them │
│ as drift, and a restore will not put back what was never taken.              │
╰──────────────────────────────────────────────────────────────────────────────╯

Format        sqlarchive, version 1
Written by    SqlArchive 0.1.0
Created       2026-09-07 22:30:00 +00:00
Consistency   snapshot - read from a database snapshot, so every table is the same instant
Server        SQL2022 / Developer Edition (64-bit) / 16.0.4165.4
Database      Ventas / SQL_Latin1_General_CP1_CI_AS
File          3.7 KB on disk

Schema  2 schemas
        2 tables, 1 stored procedure, 1 view

╭────────────────────┬──────┬─────────────────────┬───────┬─────────────────────────────╮
│ Table              │ Rows │ Row hash            │ Files │ Notes                       │
├────────────────────┼──────┼─────────────────────┼───────┼─────────────────────────────┤
│ [audit].[Log]      │    - │ no data             │     0 │ schema only, rows not       │
│                    │      │                     │       │ archived                    │
│ [dbo].[Customer]   │    3 │ 94eb66adebdc91df... │     1 │                             │
│ [dbo].[Order]      │    2 │ 3d5e6705ae893fcd... │     1 │ filtered: Total > 0         │
╰────────────────────┴──────┴─────────────────────┴───────┴─────────────────────────────╯
3 tables, 5 rows declared.
```

It says the things a partial archive has to say out loud — which tables were filtered,
which were archived without their rows, and which columns the format does not carry
(`rowversion`, computed columns and the period columns of a versioned table, because
SQL Server refuses to be told what those are).

**It reads dbdumper's archives too**, and says what they cannot tell you: dbdumper's
manifest carries no per-table row hash, so a verify against one can compare the schema
and the row counts and nothing further. It cannot see an `UPDATE`, which is the reason
SqlArchive does not write that format.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | The command did what it says it does. |
| `1` | It tried and failed: a bad path, a refused value, an unreadable archive. |
| `2` | The verb exists in the help and is not built yet. |

`2` is separate from `1` on purpose: *"this tool cannot do that yet"* and *"this run went
wrong"* are different answers, and a script should not have to read the message to tell
them apart.

---

## Roadmap

Phase 2 is the four verbs, in work packages numbered as `DESIGN.md` numbers them:

| | | State |
|---|---|---|
| **2.1** | The format: manifest, phases, JSONL, value encoding, dbdumper reader | **Done** |
| **2.2** | Export: filters, ranges, resume, consistency modes, hashes | In progress |
| **2.3** | Import: the three modes, the migration with a diff, the exact row guard | Waiting on 2.2 |
| **2.4** | Verify and inspect | `inspect` done; `verify` waiting on 2.2 |
| **2.5** | CLI, README, pocket guide, CI, release | **Done** |

Consistent subsetting by foreign key, and masking, are **Phase 4**. The manifest already
carries `rowFilter` per table so that adding them will not need a new format version.

## Related projects

| | |
|---|---|
| [**SQLDiff**](https://github.com/peopleworks/SqlSchemaDiff) | Compares two databases and writes the migration script. The schema engine here. |
| [**SyncJob**](https://github.com/peopleworks/syncjob) | Moves data between databases with a staged, guarded commit. The data engine here. |
| [**DBFSync**](https://github.com/peopleworks/DBFSync) | The same idea for DBF files. |

## Credit

The archive's shape and its value-encoding table are adopted from
[dbdumper](https://github.com/JeePeeTee/dbdumper) by JeePee (MIT), with thanks. No code is
copied; SqlArchive reads dbdumper's `manifest.json` so an archive made with it can be
verified and restored here.

## License

MIT — see [LICENSE](LICENSE).
