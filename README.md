# WorkHub

Lightweight Windows desktop utility (C# / WPF / SQLite) that gives an internal development
team quick access to repositories, releases, internal tools, project locations, links and notes.

Local-first, low resource use, no background services.

## Structure

| Project | Layer | Depends on |
| --- | --- | --- |
| `src/WorkHub.Core` | Domain model + application use cases (no UI, SQLite or file formats) | – |
| `src/WorkHub.Infrastructure` | SQLite (`Microsoft.Data.Sqlite`), JSON settings, filesystem, process launching, logging | Core |
| `src/WorkHub.Presentation` | View models + MVVM helpers, WPF-free (dialogs via `IDialogService`) | Core |
| `src/WorkHub.App` | WPF views, WPF dialog service, composition root (`WorkHub.exe`) | Core, Infrastructure, Presentation |
| `tests/WorkHub.Tests` | xUnit tests for Core, Infrastructure and Presentation (no window, no external process) | Core, Infrastructure, Presentation |

## Repository settings

Configure in **Settings → Repository** (SVN / Git):

- SVN host, SVN SYSTEM / SVN ORACLE / RELEASE prefixes, path to `TortoiseProc.exe`
- GitBucket host, GIT SYSTEM / GIT ORACLE prefixes

Generated target = `host` + `/` + `prefix` + `SystemCode`, unless a System's repository membership has a custom target.

A **new** `settings.json` starts with the approved configuration (an existing file is never changed or filled in):

| Setting | Initial value |
| --- | --- |
| SVN host | `http://10.194.105.25/svn/` |
| SVN SYSTEM / SVN ORACLE prefix | `GeSL_doc_` |
| RELEASE prefix | `GeSL_rel_` (unchanged default, on the SVN host) |
| TortoiseProc.exe | `C:/Program Files/TortoiseSVN/bin/TortoiseProc.exe` (a starting value; a missing program is still reported) |
| GitBucket host | `http://10.194.105.25:8080/gitbucket/root/` |
| GIT SYSTEM / GIT ORACLE prefix | `GeSL_src_` |

For SystemCode `4000000002` this gives `http://10.194.105.25/svn/GeSL_doc_4000000002` (SVN SYSTEM/ORACLE) and
`http://10.194.105.25:8080/gitbucket/root/GeSL_src_4000000002` (GIT SYSTEM/ORACLE).

## Initial Systems

A **new** environment starts with the approved System master data (75 Systems, 192 repository memberships: 42 SYSTEM
entries in SVN SYSTEM, GIT SYSTEM and RELEASE; 33 ORACLE entries in SVN ORACLE and GIT ORACLE). The data is embedded in
`WorkHub.Infrastructure.dll` (`Seed\InitialSystems.xml`) and loaded with the same rules as the legacy XML import.

"New" means there is no database file yet (or only an empty SQLite file). The new database is built completely in
`Data\workhub.db.new` (schema, then the initial Systems in one transaction) and only then moved into place, so a failed
or interrupted first start keeps nothing and the next start simply tries again. An existing database is never seeded,
even if its Systems table is empty, and Systems the user removed are not recreated.

## Owner Manager

User-defined launcher groups below the fixed Repository groups (schema v2). Items open a web page (http/https),
a file (default app), a folder (Explorer) or a program (.exe started directly, no shell). Targets are used as entered;
WorkHub never deletes or modifies them. Passwords are never stored; `CredentialId` is reserved for a future vault.

## Workspace

The **Workspace** tab is a logical tree of Workspace Folders and Projects (schema v3). It is WorkHub metadata only:
creating, renaming or moving a folder or project never creates, moves, renames or deletes anything on disk.

- Folders can be nested; Projects sit at the top level or in a folder and cannot contain tree children.
- Folders and projects share one name space per location. Names keep their spelling, case and Unicode text (up to 200
  characters) but are compared ignoring case and width (NFKC + upper case), so `Tools`, `TOOLS` and `ＴＯＯＬＳ` clash.
- A Project has an internal, immutable id, a status (Planning, Active, Maintenance, On Hold, Completed; default Active)
  and optional Owner, Project manager (free text, 100 characters each) and Description (4000 characters).
- **Move to…** moves a folder or project to another folder or the top level; a folder cannot move into itself or one of
  its subfolders, and a name clash at the destination is rejected without changing anything.
- The tree loads when the tab is first opened, and a folder's contents load when it is first expanded.

### Workspace Trash (schema v7)

**Move to Trash** (toolbar, context menu, the details pane or **Del** on a project; always confirmed) moves a whole
Project to the Workspace Trash. Only its marker changes: its details, project folders, links, notes and its own
project Trash entries stay exactly as they are (names, content, note revisions, timestamps) and are read-only while it
is in the Trash. Nothing on disk is touched.

- The project leaves the tree only once the move is saved; while it is saving, it stays selectable and its content stays
  usable. A different selection, its content and any open editor are never affected when the move completes.
- **Undo** (in the banner) restores it. Undo is offered only while you stay on the Workspace tab: leaving the tab (or
  Retry) withdraws it, and a move that completes after you left offers none. The project then selects itself only if you
  have not selected anything else since pressing Undo.
- **Trashed projects (n)** (above the tree) lists them with the folder they came from and when; **Restore** (or Enter)
  puts the project back into that folder — by identity, wherever that folder is now — or the top level. A folder or
  project with the same name there refuses the restore; nothing is renamed, created or moved. Restoring never changes the
  selection; **Back** (or selecting a tree node) returns to it.
- A project in the Trash reserves no name and is hidden from the tree, search and Move destinations. Acting on a project
  another window moved to the Trash says "This project is in the Trash." and removes it from this tree. A note editor
  keeps your text: Copy draft works, Save as new note is not offered (it would write into the same project).
- **Remove folder** (or **Del** on a folder) permanently removes an **empty** Workspace Folder after confirmation. It is
  refused, with the reason and without a dialog, while the folder holds anything — including a project in the Trash,
  because restoring needs its original folder. Workspace Folders are never trashed, and there is no Undo for removal.
- Projects and their content are never permanently deleted; there is no Empty Trash or automatic cleanup.

### Project content (schema v4–v6)

Selecting a Project shows its **Content** below the details: one container at a time (the Project root or one project
folder) with a breadcrumb. Project folders, links and notes never appear in the main tree and never touch the disk.

- **Project folders** nest inside one Project. Names follow the Workspace rules and are unique among sibling folders.
- **Links** point to a Folder, File (full local or UNC path) or Web page (http/https). Link names may repeat. Only
  surrounding spaces are trimmed; the target is otherwise stored exactly as typed. **Open** hands the target to
  Explorer, the default app or the browser; a link never creates, moves, renames or deletes its target.
- **Move to…** stays within the Project. If someone else moved the item after the dialog opened, nothing moves and the
  dialog says so.
- **Check** / **Check all** are manual (no polling or watchers). *Missing* means the location answered "not found";
  *Unreachable*, *Inaccessible*, *Timed out*, *Location busy* and *Checker busy* mean it could not be verified. At most
  four checks run at once (one per drive or server); each ends within 18 seconds. Web links are never contacted
  (*Not checked (web)*). Results are kept only until WorkHub closes.
- **Notes** (schema v5) are plain text at the Project root or in a project folder, listed after folders and links.
  Selecting a note shows a read-only preview below the list; Enter, double-click, Open or F2 opens the editor. Titles
  follow the Workspace name rules (they may repeat); content may be empty, up to 100,000 characters, and is stored
  exactly as typed (spaces, tabs and line breaks kept).
- The note editor saves only on **Save** or **Ctrl+S** (no autosave) and stays open. Closing with unsaved changes asks
  Save / Don't save / Cancel; a failed save never loses the text. If the note was changed or removed elsewhere, nothing
  is overwritten: **Copy draft** puts the text on the clipboard and **Save as new note** saves it next to the original
  (or where the note last was), never anywhere else. After the editor closes, the list re-reads the notes it touched.
- Keyboard: Enter opens, F2 edits, Del moves to the Trash, Backspace or Alt+Up goes up.

### Trash (schema v6)

**Move to Trash** (toolbar, context menu or **Del**, always confirmed) removes a project folder, link or note from the
listing. A folder goes to the Trash together with everything still in it, as one entry. Only WorkHub's own records
change: files, folders and web pages a link points to are never moved, renamed or deleted.

- The confirmation shows how many items the folder contains at that moment. If another window changes the folder before
  the move happens, whatever it contains then goes with it; the message afterwards says exactly how many items moved.
- **Undo** in that message restores it at once. Undo is offered only while the same folder view is still shown.
- **Trash (n)** opens the project's Trash: each entry with the folder it came from, its item count and time.
  **Restore** puts it back into that folder, wherever the folder is now. Restore never creates, restores or renames
  anything else: if the folder is itself in the Trash ("Restore “Drafts” first"), no longer exists, or already has a
  folder with the same name, nothing changes and the entry stays in the Trash.
- An item put in the Trash separately keeps its own entry; restoring its parent folder does not restore it.
- Items in the Trash keep their names, content and note revision, and are read-only. They are hidden from listings,
  Move destinations and search; a folder in the Trash reserves no names. Acting on an item another window moved to the
  Trash says "This item is in the Trash." A note editor keeps your text when its note was moved to the Trash: Copy draft
  works, and Save as new note saves next to the original only if that folder is not in the Trash.
- There is no permanent delete, Empty Trash or automatic cleanup: the Trash keeps everything until it is restored.

Not available yet: permanent delete of projects or content, Archive, tags, rich text, drag-and-drop, manual ordering,
autosave and moving content between projects.

### Workspace search

**Ctrl+F** on the Workspace tab (or the box above the tree) searches the whole local Workspace: folder and project names,
a project's Owner, Project manager and Description, project-folder names, link names and targets, and note titles and
content. Systems and Owner Manager items are not included (the Repository tab keeps its own search and Ctrl+F).

- Words are separated by spaces (including the full-width space); every word must appear, each in any of an item's
  fields. Matching ignores case and width the same way Workspace names do (NFKC + upper case), so `abc` finds `ＡＢＣ` and
  `ｶﾀｶﾅ` is found by `カタカナ`; hiragana and katakana stay distinct. At most 8 words and 200 characters are used.
- While the box has text, the results replace the tree (at most 200, names first); Esc clears the box and shows the tree
  again. Each result shows where the item is and what matched, with a line of text for descriptions, targets and notes.
- Enter or double-click **reveals** the item: the tree selects its folder or project, the content pane opens the folder
  the item is in now and selects it (a note shows its preview). Nothing is opened or launched; press Enter again on the
  revealed row for that.
- Results are a snapshot. Revealing always re-reads where the item is now: if it was moved, the reveal goes there; if it
  no longer exists, the result is removed. Items in the Trash — and everything in a project in the Workspace Trash — are
  never found; revealing one found earlier says "This item is in the Trash." (or "This item's project is in the Trash.")
  and removes that result. A reveal updates only the folders on the item's path (including ones
  another window changed); other folders the tree already loaded are not re-read, and re-expanding a loaded folder does
  not refresh it either.
- Search only reads. While another program or window holds the database for writing, the search retries for up to 30
  seconds and says "Waiting for the database…"; typing again or clearing cancels it within about a second.

## Legacy XML import

**Settings → Data → Import Legacy XML** reads an old WorkHub `HubData` file once, on demand. Only `SystemCode`,
`SystemName` and the section are imported: `SYSTEM` items join SVN SYSTEM, GIT SYSTEM and RELEASE; `ORACLE` items join
SVN ORACLE and GIT ORACLE. A read-only preview shows counts, conflicts and invalid entries before anything is written;
the import itself is one transaction. Existing names, disabled memberships and custom targets are never changed, and
re-importing the same file adds nothing. SQLite stays the source of truth — the XML is not synchronized or exported.

## Isolated data root (`--data-root`)

`WorkHub.exe --data-root C:\WorkHubTest` runs WorkHub against a separate data folder instead of
`%LOCALAPPDATA%\WorkHub` (for testing or trying things out). Without arguments nothing changes.

The path must be a full local path whose parent exists. The folder must be new, empty, or an existing WorkHub data
folder. It may not be (or overlap) the normal `%LOCALAPPDATA%\WorkHub` folder. Arguments are checked before anything
is created; an invalid or unknown argument shows an error and exits (code 2). WorkHub never falls back to the normal
profile when an override was given. The header shows "Test data root: …" and the choice is logged in that root's log.

## Build and test

```powershell
dotnet build WorkHub.sln
dotnet test WorkHub.sln
```

Requires the .NET 10 SDK on Windows.

## Local data

On every start WorkHub ensures `%LOCALAPPDATA%\WorkHub\` contains:

```
Data\workhub.db        SQLite database (schema version in PRAGMA user_version)
Config\settings.json   application configuration (safe defaults, no secrets)
Logs\                  daily diagnostic logs
Backups\               database backups: Daily\ (daily backups) and Migrations\ (pre-migration copies)
```

`Security\credentials.whvault` is **not** created until Password Manager setup (later phase).
An existing database that cannot be opened, fails SQLite's quick integrity check, has a newer schema version, or is a
SQLite file with schema version 0 stops startup and is never replaced. Startup does **not** compare the schema with
WorkHub's own definition: a file at the current version that passes the quick check is used as it is.

## Database backups

**What is protected.** Only the database, `Data\workhub.db`: Systems and memberships, Owner groups and items, the
Workspace tree, Projects (including those in the Workspace Trash), Project details, folders, links, notes (with their
revisions) and Project Trash entries. **Not protected:** `Config\settings.json` (repository hosts and prefixes, the
TortoiseProc path — recreated with defaults if lost), `Logs\`, and the files, folders and web pages that links and Owner
items point to (WorkHub stores only their paths and addresses).

**Daily backups (`Backups\Daily\`).** On the first start of each calendar day in Japan time (Asia/Tokyo, whatever the
computer's own time zone), after the database has been opened or upgraded, WorkHub makes one backup:

- It reads a consistent snapshot through SQLite (never a plain file copy), writes it to a temporary file, makes that file
  standalone (no `-journal`, `-wal` or `-shm` file belongs to it) and fully validates it — SQLite integrity check,
  foreign keys, the exact schema definition of this WorkHub version, and the same row counts as the database. Only then
  is it renamed to `workhub_daily_<day>_v<schema>_<time>.db`. An existing file is never overwritten.
- The live database is only read: its journal mode and its own `-journal`/`-wal`/`-shm` files are never changed.
- If another WorkHub window on the same data folder is already making today's backup, this start skips it. A failed
  backup (database busy for more than 10 seconds, disk full, folder not writable, failed check) shows a notice, removes
  nothing, and is retried at the next start that day. Startup itself never fails because of a backup.
- WorkHub keeps the **3 most recent fully validated** daily backups. It removes an older backup only after it has just
  made a new one, has confirmed three newer backups by a full check, and the old backup itself passes the full check.
  Damaged, unrecognized and future-dated files are never removed; neither are pre-migration backups or anything else in
  `Backups\`. Leftover temporary files of an interrupted backup are removed by the next backup.
- A clock set to the wrong day can create a backup under that day; such files are kept, and a file dated in the future
  is neither counted nor removed.

**Pre-migration backups (`Backups\Migrations\`).** Before a schema upgrade WorkHub makes the same kind of standalone,
validated copy at the old version. If it cannot, the database is **not** upgraded and startup stops, as before.

**Settings → Backups** lists both folders and shows, for each backup, its date (Japan time), schema version, size,
status and whether this version can use it. *Quick-checked* is a light check made when the list loads; **Validate**
runs the full check (*Fully validated*). The page only reads: it cannot restore, replace or delete anything.

### Recovering from a backup (manual, advanced)

WorkHub does not restore backups itself. If you need to go back to a backup, follow these steps exactly. Each step is
yours; there is no recovery journal that finishes an interrupted step for you.

What the next start checks: WorkHub opens `Data\workhub.db`, runs SQLite's quick integrity check and reads the schema
version. It refuses (and leaves unchanged) a file that cannot be opened, fails that check, has a newer version, or is a
SQLite file at version 0; it upgrades an older version after making a pre-migration backup. It does **not** check that
a current-version file has WorkHub's schema — a foreign or altered file at the current version would be used. It treats
an empty file as an interrupted first start and replaces it, and if `workhub.db` is **missing** it creates a new
database with only the initial Systems. Opening the database also makes SQLite apply any `workhub.db-journal` or
`workhub.db-wal` lying next to it — even one that belongs to a different database. Hence:

1. While WorkHub is running: **Settings → Backups**, select the backup, click **Validate**. Continue only if it shows
   **Fully validated** and its version is *Current* or *Older*. Never use a backup that is only *Quick-checked*, *Not
   standalone*, *Structure differs*, *Not a WorkHub database*, *Newer WorkHub version* or damaged.
2. Close **every** WorkHub window that uses this data folder; check that no `WorkHub.exe` is still running.
3. Create a folder outside `Data\` for your current data (for example `Backups\Recovery-<date>\`). **Move** — do not
   copy — `Data\workhub.db` together with every `Data\workhub.db-journal`, `workhub.db-wal` and `workhub.db-shm` that
   exists into it. These files belong together: never separate, edit or delete them.
4. Check that `Data\` now contains no `workhub.db*` file.
5. Copy the chosen backup into `Data\` under a temporary name, for example `workhub.db.copying`, and check that the copy
   finished and has the same size as the backup.
6. Rename `workhub.db.copying` to `workhub.db`.
7. Start WorkHub. An older backup is upgraded (with its own pre-migration backup). Settings are not affected.

If something goes wrong:

| Situation | What to do |
| --- | --- |
| Step 3 was interrupted (some files moved, some not) | Do not start WorkHub. Move the moved files back so the database and its `-journal`/`-wal`/`-shm` files are together in `Data\` again, then start over. |
| The copy in step 5 was interrupted | `workhub.db` does not exist yet. Do **not** start WorkHub (it would create a new, nearly empty database). Delete `workhub.db.copying` and repeat step 5. |
| WorkHub was started while `workhub.db` was missing | It created a new database. Close WorkHub, move that new `workhub.db` and its `-journal`/`-wal`/`-shm` files aside, and repeat steps 5–6. Your recovery folder is unaffected. |
| The restored database is refused at startup | It is left unchanged. Close WorkHub, move it aside, and choose another backup or undo the recovery. |
| Undo the whole recovery | Close WorkHub, move `Data\workhub.db*` aside, and move all files from your recovery folder back into `Data\` together. |
