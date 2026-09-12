@echo off
echo.
echo  FluxLab -- building release...
echo.
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo  ERROR: .NET SDK not found. Install from https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)
dotnet publish "%~dp0SimpleFitsViewer\SimpleFitsViewer.csproj" -c Release -r win-x64 --self-contained true --verbosity minimal
if %errorlevel% neq 0 (
    echo.
    echo  Build failed -- see errors above.
    pause
    exit /b 1
)
echo.
echo  Done! Distributable output is in:
echo    %~dp0SimpleFitsViewer\bin\Release\net8.0\win-x64\publish\
echo.
echo  Copy that folder to any Windows x64 machine and run SimpleFitsViewer.exe
echo  No .NET installation required on the target machine.
echo.
start "" "%~dp0SimpleFitsViewer\bin\Release\net8.0\win-x64\publish\"
pause
