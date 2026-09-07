# PeopleWorks.SqlArchive.Core

Reads and writes the **SqlArchive** format: a manifest carrying a full schema snapshot and
a per-table content hash, phased executable SQL, and one JSONL file per table.

It is the engine behind exporting a SQL Server database to a readable archive, restoring
it into an existing database as a migration, and verifying that the two match — with a
real diff rather than a row count, so it sees an `UPDATE`.

It composes two other packages rather than carrying engines of its own:
[`PeopleWorks.SqlSchemaDiff.Core`](https://www.nuget.org/packages/PeopleWorks.SqlSchemaDiff.Core)
for schema and
[`PeopleWorks.SyncJob.Core`](https://www.nuget.org/packages/PeopleWorks.SyncJob.Core)
for data.

The archive's shape and its value-encoding table are adopted from
[dbdumper](https://github.com/JeePeeTee/dbdumper) by JeePee (MIT), with thanks.

See [the repository](https://github.com/peopleworks/SqlArchive) for the design and the
format specification.
