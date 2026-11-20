# SQLite 3.54.1

A database in one file, or in memory, asked in SQL. <https://www.sqlite.org/cintro.html>
introduces the functions.

```phi
use vendor.sqlite;

phi.Notes:Program
{
	ptr<sqlite3> db: 0;
	ptr<sqlite3_stmt> st: 0;
	int r: (Sql.open: 'notes.db' addr db);
	r is (Sql.exec: db 'create table if not exists notes(said text)' 0 0 0);
	r is (Sql.prepare_v2: db 'insert into notes values (?)' (-1) addr st 0);
	r is (Sql.bind_text: st 1 'written from PHI' (-1) Sql.TRANSIENT);
	r is (Sql.step: st);
	r is (Sql.finalize: st);
	r is (Sql.prepare_v2: db 'select said from notes' (-1) addr st 0);
	while (Sql.step: st) is Sql.ROW
		log (Sql.column_text: st 0) '\n';
	;;
	r is (Sql.finalize: st);
	r is (Sql.close: db);
	exit 0;
}
```

- A function is `Sql.name` for C's `sqlite3_name` (`Sql.open`, `Sql.prepare_v2`), and a
  constant `Sql.NAME` for `SQLITE_NAME` (`Sql.OK`, `Sql.ROW`, `Sql.DONE`).
- `Sql.TRANSIENT` and `Sql.STATIC` are what `bind_text` and `bind_blob` are told about the
  bytes they're given: with `TRANSIENT`, SQLite copies them.
- A function SQL calls, or one `exec` gives each row to, is a PHI method given as
  `addr foreign Name` ([tests/vendor/sqlite_program.phi](../../tests/vendor/sqlite_program.phi) has both).
- Built with full-text search (FTS5), R-trees and the maths functions, safe to use from
  several threads.

Built from SQLite's own source, unchanged. Public domain ([LICENSE](LICENSE)).
