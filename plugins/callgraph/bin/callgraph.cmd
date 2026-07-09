@echo off
rem CallGraph plugin launcher (Windows). Dispatches to the bundled win-x64 binary.
setlocal
set "EXE=%~dp0win-x64\CallGraph.exe"
if not exist "%EXE%" (
  echo callgraph: no bundled binary at "%EXE%" 1>&2
  echo Build it with: plugins\callgraph\scripts\build-binaries.sh win-x64 1>&2
  exit /b 1
)
"%EXE%" %*
