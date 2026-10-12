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

### Project content (schema v4–v5)

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
- Keyboard: Enter opens, F2 edits, Backspace or Alt+Up goes up.

Not available yet: delete, Trash, Archive, tags, search, rich text, drag-and-drop, manual ordering, autosave and moving
content between projects.

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
