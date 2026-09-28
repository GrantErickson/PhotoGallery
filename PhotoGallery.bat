@echo off
rem Builds Photo Gallery (quick when nothing changed) and starts it.
rem   PhotoGallery.bat          build, then start
rem   PhotoGallery.bat nobuild  start the last build without building
setlocal
set "ROOT=%~dp0"
set "PROJECT=%ROOT%src\PhotoGallery.App\PhotoGallery.App.csproj"
set "EXE=%ROOT%src\PhotoGallery.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\PhotoGallery.exe"

rem One copy at a time: a second would repeat the background work on the same library, and a running copy's files
rem can't be replaced by a build.
tasklist /fi "imagename eq PhotoGallery.exe" 2>nul | find /i "PhotoGallery.exe" >nul
if not errorlevel 1 (
    echo Photo Gallery is already running. Close it first to build and start the latest version.
    timeout /t 5 >nul 2>nul
    exit /b 0
)

if /i "%~1"=="nobuild" if exist "%EXE%" goto run

where dotnet >nul 2>nul
if errorlevel 1 (
    echo The .NET 10 SDK is needed to build Photo Gallery: https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo Building Photo Gallery...
dotnet build "%PROJECT%" -c Debug -nologo -v q
if errorlevel 1 (
    echo.
    echo The build failed; see the errors above.
    pause
    exit /b 1
)
if not exist "%EXE%" (
    echo The build finished but %EXE% is missing.
    pause
    exit /b 1
)

:run
echo Starting Photo Gallery...
start "" "%EXE%"
