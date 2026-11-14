@echo off
rem ---------------------------------------------------------------------------
rem Typography Font File Viewer - release build.
rem
rem Produces ONE self-contained single-file win-x64 exe in publish\, with the
rem runtime, WPF and the native FreeType / HarfBuzz libraries packed inside and
rem symbols embedded, so nothing has to be installed on the target PC.
rem ---------------------------------------------------------------------------
setlocal

cd /d "%~dp0"

rem Start from an empty folder. Publishing does not delete files it no longer
rem produces, so a stale dll left from an earlier configuration would otherwise
rem sit beside the exe and look like part of the deliverable.
if exist publish rmdir /s /q publish

dotnet publish src\TypographyFontFileViewer\TypographyFontFileViewer.csproj -c Release -o publish
if errorlevel 1 (
    echo.
    echo BUILD FAILED - nothing was published.
    exit /b 1
)

if not exist publish\TypographyFontFileViewer.exe (
    echo.
    echo BUILD REPORTED SUCCESS BUT THERE IS NO EXE IN publish\.
    exit /b 1
)

echo.
echo Published:
for %%F in (publish\TypographyFontFileViewer.exe) do @echo   %%~nxF  %%~zF bytes
echo.
echo Everything in publish\ (the deliverable is meant to be the exe alone):
dir /b publish
echo.
endlocal
