@echo off
rem Typography Font File Viewer - self-contained single-file publish (win-x64)
dotnet publish src\TypographyFontFileViewer\TypographyFontFileViewer.csproj -c Release -o publish
echo.
echo Output: publish\TypographyFontFileViewer.exe
