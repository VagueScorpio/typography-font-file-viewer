@echo off
rem Simple Font Viewer - self-contained single-file publish (win-x64)
dotnet publish src\SimpleFontViewer\SimpleFontViewer.csproj -c Release -o publish
echo.
echo Output: publish\SimpleFontViewer.exe
