@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul
set "PROJECT=%~dp0src\FF.AgentCollaboration\FF.AgentCollaboration.csproj"
set "EXE=%~dp0src\FF.AgentCollaboration\bin\Release\net10.0\FF.AgentCollaboration.exe"
set "FF_WORKBENCH_HOME=%~dp0."
set "PROJECT_ARGS="
if not defined KNOWLEDGE if not exist "%~dp0workbench-data\database\workbench.sqlite" if exist "%~dp0..\.project-knowledge\agent-collaboration\cache\workbench.sqlite" set "KNOWLEDGE=%~dp0..\.project-knowledge"
if not "%~1"=="" set PROJECT_ARGS=--project "%~f1"
set "PORT=5186"
if not "%~2"=="" set "PORT=%~2"

dotnet build "%PROJECT%" -c Release --nologo
if errorlevel 1 goto failed
echo [FF.Agent] 按 Ctrl+C 停止工作台。
if defined KNOWLEDGE goto knowledge
"%EXE%" workbench %PROJECT_ARGS% --port "%PORT%" --open-browser
goto done
:knowledge
"%EXE%" workbench %PROJECT_ARGS% --port "%PORT%" --knowledge "%KNOWLEDGE%" --open-browser
:done
if not errorlevel 1 exit /b 0
:failed
echo [FF.Agent] 启动失败，请检查上方提示。
pause
exit /b 1
