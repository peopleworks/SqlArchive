# Contributing to SqlArchive

Thanks for taking a look. Issues and pull requests are both welcome.

## Getting set up

```bash
git clone https://github.com/peopleworks/SqlArchive.git
cd SqlArchive
dotnet build SqlArchive.sln -c Release
dotnet test tests/SqlArchive.Core.Tests -c Release --no-build
```

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download) or newer. The unit
tests need no database: they cover the format — the value encoding, the hash, the
manifest, reading dbdumper's archives — the parts of export, import and verify that can
be tested without a server, and the CLI itself, driven through `Program.Run` with the same
parser, flags and exit codes an operator gets. A SQL Server instance (2016 or newer) is
only needed to try the CLI end to end, and to run the live tests below.

CI builds on Ubuntu and Windows and runs every test project under `tests/` except the
integration one, discovered rather than listed, so a new unit-test project runs without
touching `ci.yml`.

## Running the live tests

`tests/SqlArchive.IntegrationTests` drives the engine against a real server: exports under
each consistency mode, resumed exports, restores into an empty database and over one that
already has tables, system-versioned tables with their history, the value encoding round
tripped through SQL Server, and `verify` finding drift. Most of what the CHANGELOG lists
under *Found against a real server* was found by one of these failing, not by reading
code — the catalog and the server's refusals cannot be faked.

Point `SQLARCHIVE_TEST_CONN` at a **server**, not a database — the tests connect to
`master` and create their own. The login has to be able to create and drop databases.
The same server CI uses is one command away:

```bash
docker run -d --name sqlarchive-it -p 1433:1433 \
  -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=Str0ng!Passw0rd#CI' \
  mcr.microsoft.com/mssql/server:2022-latest

SQLARCHIVE_TEST_CONN='Server=localhost,1433;User Id=sa;Password=Str0ng!Passw0rd#CI;TrustServerCertificate=true;Encrypt=false' \
  dotnet test tests/SqlArchive.IntegrationTests -c Release
```

```powershell
# PowerShell, against a local instance with Windows authentication
$env:SQLARCHIVE_TEST_CONN = 'Server=localhost;Integrated Security=true;TrustServerCertificate=true'
dotnet test tests/SqlArchive.IntegrationTests -c Release
```

They take minutes, not seconds.

Scratch databases are named `SqlArchiveIT_<8 hex>` and dropped when the run finishes,
including after failing tests — a failed run is the one that leaves the most behind. If a
run is killed hard enough to skip the cleanup, they are safe to delete by hand; nothing
else uses that prefix. A run killed in the middle of a `--consistent` export can also
leave a database snapshot named `SqlArchiveIT_<8 hex>_sqlarchive_<8 hex>`, and a snapshot
has to be dropped before the database it is a snapshot of.

**Without the variable set, every live test reports itself as skipped** — with the reason
`SQLARCHIVE_TEST_CONN is not set` — **and the run passes.** That is deliberate:
contributors without a SQL Server still get a green `dotnet test`, and CI runs the same
tests for real against a SQL Server 2022 container. No live test is skipped when the
variable is set.

## Reporting a bug

The useful bug report for an archive tool is a **reproduction in SQL**: the
`CREATE TABLE` and the few rows that show it, the commands you ran, and what you got
against what you expected.

```sql
CREATE TABLE dbo.T (Id int NOT NULL PRIMARY KEY, Taken datetime2(3) NOT NULL);
INSERT dbo.T VALUES (1, '2026-01-15T10:00:00.003');
```

```text
sqlarchive export --source "Server=.;Database=Repro;Integrated Security=true" --out repro.sqlarchive
sqlarchive import repro.sqlarchive --destination "Server=.;Database=ReproCopy;Integrated Security=true"
sqlarchive verify repro.sqlarchive --against "Server=.;Database=ReproCopy;Integrated Security=true"

-- expected: exit 0, the two sides match
-- actual:   exit 3, [dbo].[T] differs by content at the same row count
```

That turns straight into a test. Please also include `sqlarchive --version` and your SQL
Server version (`SELECT @@VERSION`).

**When the bug is about equality** — `verify` says content differs, a hash does not
match, a value came back different — include the value and its column's **exact** type:
`datetime2(3)` and not `datetime2`, `varchar` or `nvarchar`, the collation for a string.
The [value-encoding table in FORMAT.md](FORMAT.md#the-value-encoding-table) is the
normative definition of two rows being equal: they are the same row when their encoded
bytes are the same. A report has to let us find which line of that table the value went
through, and whether it came out the way the table says.

**Never attach a real archive, or a spool left behind by an export** — both contain the
rows. `sqlarchive inspect <archive> --json` prints what the manifest says without a single
row, but it still names your server, your database and every object in it, so trim it to
the tables that matter. And never paste a connection string with a password in it.

## Pull requests

- **Add a test.** A change to the format or the encoding needs a unit test; anything that
  talks to SQL Server needs a live test as well. Where a fix exists because SQL Server
  refused something, the comment beside it names the error number and, where it was
  measured, the server version — `ForeignKeyFence` and `TemporalPublisher` show the
  pattern. It is what stops a fix from silently regressing.
- **Keep the build clean.** `TreatWarningsAsErrors` is on in both projects under `src/`.
  A doc comment is not required on every public member (`CS1591` is off on purpose); write
  one where something is not obvious.
- **Match the surrounding style.** There is no `.editorconfig`; the style is in the code.
  It writes `if(condition)` without a space — `foreach(`, `while(`, `catch(` and
  `switch(` too — uses file-scoped namespaces, and comments explain *why* rather than
  restating the code.
- **One concern per pull request.** An encoding fix and a new option are two pull
  requests.

## Changing the format is not a refactor

The archive format is versioned on its own, by `formatVersion` in every manifest, and
separately from the package version. Changing the value encoding, the string escaping,
the hash, the entry names, which columns are carried, or the manifest's shape is a
**format change**. A changed encoding changes the bytes, changed bytes change the hash,
and every archive already written stops verifying against the rows it was taken from.

So a pull request that touches any of those updates [FORMAT.md](FORMAT.md) in the same
change and makes the case about `formatVersion` there. The string escaping is owned by the
format rather than left to a JSON library for exactly this reason: a library may change
its escaping between versions, and the day it did, every hash would move with it.
FORMAT.md records the one content change that stayed at version 1 — the carried period
columns — and why it could: no reader of version 1 had shipped yet. 0.1.0 has shipped, so
that argument is no longer available.

## The public API is checked at pack time

`PeopleWorks.SqlArchive.Core` is a published library. `EnablePackageValidation` compares
it with the baseline version named in `src/SqlArchive.Core/SqlArchive.Core.csproj` —
0.1.1 today — and a public member removed or changed fails `dotnet pack`, which CI runs on
every pull request. To check before pushing:

```bash
dotnet pack src/SqlArchive.Core/SqlArchive.Core.csproj -c Release -o ./artifacts
```

Adding an optional parameter to an existing method counts. It recompiles cleanly and is a
different method in IL, which is how `PeopleWorks.SqlSchemaDiff.Core` 1.8.0 made
`PeopleWorks.SyncJob.Core` throw `MissingMethodException` in production. Below 1.0 the
public API may still change between minor versions, but as a decision a release makes on
purpose — that is what the check forces. After each release the baseline moves to the
version just published.

## The engines live in their own repositories

SqlArchive has no schema engine or data engine of its own. Extracting, diffing and
rendering the schema is
[`PeopleWorks.SqlSchemaDiff.Core`](https://github.com/peopleworks/SqlSchemaDiff); staging,
guarding and publishing the rows is [`PeopleWorks.SyncJob.Core`](https://github.com/peopleworks/syncjob).
Both are referenced as NuGet packages and never copied. **A defect in either is fixed
there** and reaches this repository as a version bump. Where SqlArchive has to work
around one in the meantime, the comment at the workaround says so and why —
`TablePublisher.ReseedAsync` is an example.

## Where the reasoning is written down

[FORMAT.md](FORMAT.md) is the normative specification of the archive, in English.
[DESIGN.md](DESIGN.md) is the design and its open gaps, in Spanish. The *Found against a
real server* section of [CHANGELOG.md](CHANGELOG.md) is the list of behaviours that only
showed up on a live server; read it before changing anything it names.

## Code of conduct

Be decent to each other. Harassment or personal attacks are not welcome, and I will
close threads that go that way. The full terms, and how to report an incident, are in
[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
