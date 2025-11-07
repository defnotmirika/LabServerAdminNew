@echo off
echo Building Lab Server Client Application...
echo.

echo Building Client Application...
dotnet build LabServerClient.sln --configuration Release
if %errorlevel% neq 0 (
    echo Client build failed!
    pause
    exit /b 1
)

echo.
echo Client build completed successfully!
echo.
echo To run the client application:
echo dotnet run --project LabServerClient\LabServerClient.csproj
echo.
echo Or run the executable:
echo LabServerClient\bin\Release\net8.0-windows\LabServerClient.exe
echo.
pause
