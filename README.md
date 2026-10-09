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

Not available yet: delete, Trash, Archive, project folders, links, notes, tags, search, drag-and-drop and manual ordering.

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
Backups\               database backups (Backups\Migrations\ for pre-migration copies)
```

`Security\credentials.whvault` is **not** created until Password Manager setup (later phase).
An existing database that is unreadable, corrupt, foreign or from a newer schema stops startup
and is never replaced.
