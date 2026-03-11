# CallGraph Development Guide

## Overview

CallGraph is a .NET 10 CLI that indexes C# solutions with Roslyn into a local SQLite database and provides fast CLI analysis commands.

## Build and Run

```bash
dotnet build CallGraph.csproj

# Index once
dotnet run --project CallGraph.csproj -- --index "/path/to/solution.sln"

# Reindex
dotnet run --project CallGraph.csproj -- --reindex "/path/to/solution.sln"

# Watch
dotnet run --project CallGraph.csproj -- --watch "/path/to/solution.sln"
```

## Analysis Commands

```bash
dotnet run --project CallGraph.csproj -- list-solutions
dotnet run --project CallGraph.csproj -- search-file --pattern "*Controller.cs"
dotnet run --project CallGraph.csproj -- search-method --keywords "login authentication"
dotnet run --project CallGraph.csproj -- analyze --filepath "/abs/path/to/File.cs" --depth 1
dotnet run --project CallGraph.csproj -- list-unused --projectPath "/abs/path/to/Project.csproj" --filePath "/abs/path/to/File.cs"
dotnet run --project CallGraph.csproj -- list-warnings --projectPath "/abs/path/to/Project.csproj" --filePath "/abs/path/to/File.cs"
```

## Project Structure

- `Core/Indexing` - indexing pipeline and SQLite store
- `Core/Analysis` - graph analysis and streamlined JSON responses
- `Core/Diagnostics` - warning/unused diagnostic collection
- `Core/Solutions` - solution/project loading and cache
- `Core/Watching` - file-system watcher and incremental reindexing
- `Contracts` - request/response contracts
- `Program.cs` - CLI entrypoint and subcommand handling

## Key Behavior

- Test projects are excluded from indexing/analysis.
- Visibility modes for analysis:
  - `external`: class-based depth
  - `internal`: method-based depth
- `list-unused` and `list-warnings` reuse solution context cache when possible.

## Testing

```bash
dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj
```
