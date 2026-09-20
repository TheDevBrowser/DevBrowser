# Protected saved REST requests

Saved request authentication tokens/passwords, usernames, URLs, query parameters,
headers and bodies are now stored in Windows DPAPI-protected files under
`%LocalAppData%\DevBrowser\secrets`. SQLite stores an opaque reference. Request names,
descriptions, methods, content/authentication types and collection metadata remain
in SQLite, so do not put secrets in names or descriptions.

The REST editor, URL search, duplication, moves and explicit full exports continue
to receive the original values in memory. Existing `{{VARIABLE}}` references are
preserved; use secret environment variables for reusable credentials in headers,
URLs, parameters and bodies.

## Migration and recovery

On startup, old rows are migrated automatically. The service writes an immutable
protected payload and verifies it can be read before committing the reference and
clearing the old plaintext columns. A storage failure or failed database commit
leaves the original database row available for retry. Updates likewise preserve
the previous protected payload until the database commit succeeds. Concurrent
reference changes cause a database concurrency error rather than silently
overwriting another save. Secret files are written using a temporary encrypted
file and atomic replacement.

Keep the database and secret directory together in backups. Restoring only the
database, deleting secret files or switching Windows accounts can make saved
requests unreadable. Missing or undecryptable data is reported as an error; it is
never silently replaced with blank credentials. Old app versions do not understand
the protected references; do not downgrade against the migrated database.

Successful edits/deletions remove superseded protected files. A crash, failed
database commit or file cleanup failure can leave unreferenced encrypted files.
These are intentionally not automatically pruned: another operation could still
be committing its reference.

Migration enables SQLite secure_delete and startup attempts a truncating WAL
checkpoint. This does not guarantee forensic erasure from old backups, filesystem
snapshots, historical free pages, or journals held by another open connection.
Rotate credentials if their previous plaintext storage may have exposed them.

DPAPI protects stored bytes for the current Windows user. It does not protect
against malicious code running as that user or reading the application's memory.
Exports are separate from encrypted storage: full exports intentionally contain
plaintext; current default collection export redaction still has the gaps tracked
under SEC-04. Do not assume a collection file is safe to publish.

## Verification

Run `dotnet run --project tests/DeveloperBrowser.SecurityChecks/DeveloperBrowser.SecurityChecks.csproj`
on Windows. Checks use an isolated temporary database and real DPAPI. They cover
storage bytes, reload/search/duplicate/update/delete, schema migration, failed
secret writes, failed database commits, missing payloads and export/import.
The build workflow runs these checks as well.
