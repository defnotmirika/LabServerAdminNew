@echo off
echo Building Lab Server Admin Application...
echo.

echo Building Admin Application...
dotnet build LabServerAdmin.sln --configuration Release
if %errorlevel% neq 0 (
    echo Admin build failed!
    pause
    exit /b 1
)

echo.
echo Admin build completed successfully!
echo.
echo To run the admin application:
echo dotnet run --project LabServerAdmin\LabServerAdmin.csproj
echo.
echo Or run the executable:
echo LabServerAdmin\bin\Release\net8.0-windows\LabServerAdmin.exe
echo.
pause
