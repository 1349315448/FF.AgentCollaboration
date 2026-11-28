@echo off
setlocal EnableExtensions DisableDelayedExpansion
"%~dp0FF.AgentCollaboration.exe" workbench-managed --open-browser %*
set "exitCode=%errorlevel%"
if not "%exitCode%"=="0" pause
exit /b %exitCode%
