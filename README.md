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

Configure in **Settings → Repository** (SVN / Git). Nothing site-specific ships in source:

- SVN host, SVN SYSTEM / SVN ORACLE / RELEASE prefixes, path to `TortoiseProc.exe`
- GitBucket host, GIT SYSTEM / GIT ORACLE prefixes

Generated target = `host` + `/` + `prefix` + `SystemCode`, unless a System's repository membership has a custom target.

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
