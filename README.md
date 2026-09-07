<div align="center">

# 🗃️ SqlArchive

**Export a SQL Server database to a readable archive. Restore it somewhere else. Prove they match.**

[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![SQL Server](https://img.shields.io/badge/SQL%20Server-2016%2B-CC2927?style=flat-square&logo=microsoftsqlserver&logoColor=white)](https://www.microsoft.com/sql-server)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![PeopleWorks](https://img.shields.io/badge/by-PeopleWorks-636f61?style=flat-square)](https://mvp.microsoft.com/en-US/mvp/profile/24060a02-dbc6-44ec-bca5-c213ff9835c5)

**Design in progress — [DESIGN.md](DESIGN.md) is the current reference.**

</div>

---

## What it is

The fourth tool in the PeopleWorks database family. [SQLDiff](https://github.com/peopleworks/SqlSchemaDiff)
moves schema and [SyncJob](https://github.com/peopleworks/syncjob) moves data; SqlArchive
moves a whole database, through a file you can read, compare and verify.

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

## Credit

The archive's shape and its value-encoding table are adopted from
[dbdumper](https://github.com/JeePeeTee/dbdumper) by JeePee (MIT), with thanks. No code is
copied; SqlArchive reads dbdumper's `manifest.json` so an archive made with it can be
verified and restored here.
