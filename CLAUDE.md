# CallGraph Development Guide

## Overview

CallGraph is a .NET 10 CLI that indexes C# solutions with Roslyn into a local SQLite database and provides fast CLI analysis commands.

## Purpose

CallGraph is designed to improve coding-agent precision and reduce token usage for C# work:

- Agents and developers run targeted local `query`/`analyze` commands against a pre-built index instead of scanning large code chunks in prompts.
- A bundled `_claude` template with two skills (`callgraph-sql`, `callgraph-analyze-callgraph`) standardizes how agents invoke CallGraph workflows.
- The result is faster, more accurate code navigation and dependency understanding across large solutions.

## Build and Run

```bash
dotnet build CallGraph.csproj

# Index once
dotnet run --project CallGraph.csproj -- --index "/path/to/solution.sln"

# Reindex (git-aware incremental)
dotnet run --project CallGraph.csproj -- --reindex "/path/to/solution.sln"

# Clear the index
dotnet run --project CallGraph.csproj -- --clear
```

## Analysis Commands

```bash
dotnet run --project CallGraph.csproj -- query "SELECT COUNT(*) FROM Methods"
dotnet run --project CallGraph.csproj -- analyze --filepath "/abs/path/to/File.cs" --depth 1
```

## Project Structure

- `Core/Indexing` - indexing pipeline and SQLite store
- `Core/Analysis` - graph analysis and streamlined output
- `Core/Git` - git-aware incremental reindexing support
- `Core/Projects` - project loading
- `Core/Solutions` - solution loading and cache
- `Core/Output` - CLI output formatting
- `Contracts` - request/response contracts
- `Cli` - CLI entrypoint, argument parsing, and subcommand execution
- `Program.cs` - process entrypoint

## Key Behavior

- Test projects are excluded from indexing/analysis.
- Visibility modes for analysis:
  - `external`: class-based depth
  - `internal`: method-based depth
- `query` runs read-only SQL against the indexed SQLite database; the `callgraph-sql` skill documents the schema (7 tables) and example queries.
- `analyze` performs recursive call-graph traversal; the `callgraph-analyze-callgraph` skill documents usage.

## Testing

```bash
dotnet test tests/CallGraph.Tests/CallGraph.Tests.csproj
```
