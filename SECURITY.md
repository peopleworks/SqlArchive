# Security policy

## Reporting a vulnerability

Open a [private security advisory](https://github.com/peopleworks/SqlArchive/security/advisories/new),
or email **peopleworks@gmail.com** with `SQLARCHIVE SECURITY` in the subject.
Please do not open a public issue for a vulnerability.

Include the version (`sqlarchive --version`), your SQL Server version, what you ran, and
what happened. **Never attach a real archive, a spool directory, or a connection string**
— an archive is a copy of the data. You will get a first response within a week.

## Supported versions

| Version | Supported |
|---|---|
| 0.1.x | Yes |

## Handling credentials

SqlArchive connects to SQL Server with a connection string you supply, and in 0.1.0 it
takes one **only as a command-line argument**: `--source` for `export`, `--destination`
for `import`, `--against` for `verify`. There is no connection file and no
environment-variable indirection yet. That is a known gap.

**A password passed that way is not private**: other processes on the machine can read
the full command line, shells record it in history, and CI runners usually echo the
command. Until indirection exists:

- Use Windows authentication, `Integrated Security=true`, which puts no password anywhere.
- In CI, pass the connection string from a secret that the runner expands. That keeps it
  out of the workflow file and, on most runners, masked in the log — but it still lands
  in the process arguments while the command runs, so anything that can list processes on
  that runner can read it.

**A defect in 0.1.0, fixed for the next release:** when `export` cannot connect to or read
the source, its error message quotes the `--source` string back in full, password included.
`import` and `verify` print only the driver's message. On 0.1.0, treat the console output and
any log of a failed export as containing the connection string — and a password that went
into one as exposed.

## What SqlArchive writes

- **An archive contains the rows.** Unlike SQLDiff's scripts, a `.sqlarchive` is a copy
  of the data — every row of every table it was asked for, as readable JSON lines — and
  should be handled like a backup: kept where backups are kept, under the same access
  control. **It is not encrypted.** It is a plain zip that any unzip tool opens, which is
  the point of a readable archive, and it means protection has to come from where the
  file is stored or how it is moved. `--table`, `--exclude`, `--where` and `--schema-only`
  keep rows out of it; there is no masking in 0.1.0, so a column that is exported is
  exported as it is.
- **The manifest and the archive's `README.txt`** record where it came from: the server
  name, the database, the edition, the product version and the collation. The manifest
  also holds the whole schema, including the definitions of views, procedures, functions
  and triggers, which may be sensitive in themselves, and each `--where` predicate as
  written. **It never holds a connection string or a password.**
- **The export spool** (`--spool`, by default the archive's path with `.work` appended)
  is where rows land before they are packed, one uncompressed JSONL file per table range.
  While an export runs it is a second copy of the data. Beside the rows are
  `fingerprint.json` — the server and database in the clear, and a SHA-256 of the server,
  database and login, never the connection string — and `plan.json`, with the tables,
  their range bounds and their row filters. The spool is deleted when the export
  succeeds, and when it fails **unless `--resume` was passed**: then a failed export keeps
  it so the next run can carry on, and the copy of the data stays on disk until it is
  resumed or deleted. A directory that is not empty and was not left by an export is
  refused rather than emptied.
- **The import journal** (`--work-dir`, by default the archive's path with `.restore`
  appended) holds no rows: a fingerprint like the export's, with a hash of the archive's
  contents in place of a path; which route the restore took; a marker per finished table
  with its row count; and, once applied, the text of the schema diff. It follows the same
  rule — deleted on success, and on failure unless `--resume` was passed. `--dry-run`
  creates none.
- **`verify --json <FILE>`** writes the verdict: the archive's path, the database name as
  the server reports it, and per table the counts and hashes on each side. No connection
  string.

## What SqlArchive executes

- **`inspect`** reads the archive and never connects to a server.
- **`verify`** without `--against` reads only the archive. With it, it reads the database's
  schema and the rows of the tables it compares, and writes nothing.
- **`export`** reads the source. Without `--consistent` that is all it does. With it, it
  first tries a **database snapshot** — `CREATE DATABASE <source>_sqlarchive_<8 hex> ...
  AS SNAPSHOT OF <source>`, its sparse files placed beside the source's data files —
  reads from that, and drops it at the end. That needs `CREATE DATABASE` permission and
  room on the source's data volume. Failing that, it reads through one connection under
  `SNAPSHOT` isolation, which needs `ALLOW_SNAPSHOT_ISOLATION ON`; SqlArchive will not
  turn that on for you. If neither is available the export is refused rather than quietly
  read table by table. A snapshot that will not drop does not fail an export that has
  finished; it is reported by name, and stays on the server until someone drops it.
  `--where` predicates are your SQL and go to the server as written.
- **`import`** writes to the destination, and **a restore replaces the rows of every table
  it publishes**. It never creates the destination database; the database has to exist.
  - *Schema.* Into a database with no tables, the archive's own schema phases run around
    the data. Into one that already has tables, the schema is brought to the archive's
    shape by a SQLDiff diff that **drops nothing**: objects the destination has and the
    archive does not are left alone, and a change that cannot be made in place rebuilds
    the table with its rows kept. Schema statements run batch by batch and **not in a
    transaction**, so a schema step that fails half way leaves in place what already ran.
    `--table` and `--exclude` choose whose rows are published; the schema is applied
    whole.
  - *Data.* Each table is loaded into a staging table beside it,
    `<table>_sqlarchive_<8 hex>`, and its row count and content hash are checked against
    the manifest before anything reaches the real table. It is then published atomically:
    by `ALTER TABLE ... SWITCH` where that is possible, and by `DELETE` plus
    `INSERT ... SELECT` in one transaction where it is not — a table with a foreign key
    switched off, or one with `GENERATED ALWAYS` columns. A system-versioned table and its
    history are published together in one transaction, with versioning switched off and
    back on inside it. A table that fails or is refused holds what it held before. Identity
    counters are reseeded with `DBCC CHECKIDENT`. Staging tables are dropped afterwards;
    one that will not drop stays behind under that name.
  - *Foreign keys.* On a destination that already has them, every enabled foreign key that
    touches a table being published is switched off (`NOCHECK CONSTRAINT`) for the data
    phase and put back with `WITH CHECK CHECK CONSTRAINT`, which re-validates it against
    the restored rows. A key that fails validation is re-enabled untrusted and reported,
    with the statement that failed. A key that was already off stays off, and one that was
    already untrusted goes back untrusted.
  - *`--dry-run`* reads the destination's schema and foreign keys, works out the diff and
    what would be published, and writes nothing: no DDL, no rows, no journal.

Before restoring over a database you care about, run `import --dry-run` against it, and
afterwards `verify --against` it.
