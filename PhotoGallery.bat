@echo off
rem Starts Photo Gallery. Builds it first if it hasn't been built yet;
rem "PhotoGallery.bat build" rebuilds before starting (after pulling changes).
setlocal
set "ROOT=%~dp0"
set "PROJECT=%ROOT%src\PhotoGallery.App\PhotoGallery.App.csproj"
set "EXE=%ROOT%src\PhotoGallery.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\PhotoGallery.exe"

rem One copy at a time: a second would repeat the background work on the same library.
tasklist /fi "imagename eq PhotoGallery.exe" 2>nul | find /i "PhotoGallery.exe" >nul
if not errorlevel 1 (
    echo Photo Gallery is already running.
    timeout /t 3 >nul 2>nul
    exit /b 0
)

if /i "%~1"=="build" goto build
if exist "%EXE%" goto run

:build
echo Building Photo Gallery...
dotnet build "%PROJECT%" -c Debug -nologo -v q
if errorlevel 1 (
    echo.
    echo The build failed; see the errors above.
    pause
    exit /b 1
)

:run
start "" "%EXE%"
