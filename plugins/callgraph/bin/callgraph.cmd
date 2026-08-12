@echo off
rem CallGraph plugin launcher (Windows).
rem Prefers the user-installed binary; falls back to a locally bundled one (dev builds).
setlocal

set "USER_EXE=%APPDATA%\callgraph\bin\win-x64\CallGraph.exe"
set "BUNDLED_EXE=%~dp0win-x64\CallGraph.exe"

if exist "%USER_EXE%" (
  "%USER_EXE%" %*
  exit /b %ERRORLEVEL%
)
if exist "%BUNDLED_EXE%" (
  "%BUNDLED_EXE%" %*
  exit /b %ERRORLEVEL%
)

echo callgraph: binary not found. 1>&2
echo Run the setup skill to install it: /callgraph:callgraph-setup 1>&2
exit /b 1
