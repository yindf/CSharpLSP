@echo off
REM Publish CSharpMcp.Server as a self-contained Windows executable

setlocal

set CONFIGURATION=Release
set OUTPUT_DIR=publish
set RUNTIME=win-x64

echo Checking for running CSharpMcp.Server processes...
tasklist /FI "IMAGENAME eq CSharpMcp.Server.exe" 2>NUL | find /I /N "CSharpMcp.Server.exe">NUL
if not errorlevel 1 (
    echo Killing existing CSharpMcp.Server.exe process^(es^)...
    taskkill /F /IM CSharpMcp.Server.exe 2>NUL
    timeout /t 1 /nobreak >NUL
    echo Done.
) else (
    echo No CSharpMcp.Server.exe process found
)
echo.

echo Publishing CSharpMcp.Server for Windows x64...
echo.

dotnet publish src\CSharpMcp.Server\CSharpMcp.Server.csproj -c %CONFIGURATION% -o %OUTPUT_DIR% -r %RUNTIME% --self-contained true

if %ERRORLEVEL% EQU 0 (
    echo.
    echo Publish successful!
    echo Output: %OUTPUT_DIR%\CSharpMcp.Server.exe
    echo.
    echo To run:
    echo   %OUTPUT_DIR%\CSharpMcp.Server.exe
) else (
    echo.
    echo Publish failed!
    exit /b %ERRORLEVEL%
)

endlocal
