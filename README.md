# WorkHub

Lightweight Windows desktop utility (C# / WPF / SQLite) that gives an internal development
team quick access to repositories, releases, internal tools, project locations, links and notes.

Local-first, low resource use, no background services.

## Structure

| Project | Layer | Depends on |
| --- | --- | --- |
| `src/WorkHub.Core` | Domain model + application use cases (no UI, SQLite or file formats) | – |
| `src/WorkHub.Infrastructure` | SQLite (`Microsoft.Data.Sqlite`), JSON settings, filesystem, logging | Core |
| `src/WorkHub.App` | WPF views/view models, composition root (`WorkHub.exe`) | Core, Infrastructure |
| `tests/WorkHub.Tests` | xUnit tests for Core + Infrastructure | Core, Infrastructure |

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
