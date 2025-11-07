@echo off
echo Building Lab Server PC Management System...
echo.

echo Building Admin Application...
dotnet build LabServerAdmin.sln --configuration Release
if %errorlevel% neq 0 (
    echo Admin build failed!
    pause
    exit /b 1
)

echo.
echo Building Client Application...
dotnet build LabServerClient.sln --configuration Release
if %errorlevel% neq 0 (
    echo Client build failed!
    pause
    exit /b 1
)

echo.
echo All builds completed successfully!
echo.
echo Applications built:
echo - Admin: LabServerAdmin\bin\Release\net8.0-windows\LabServerAdmin.exe
echo - Client: LabServerClient\bin\Release\net8.0-windows\LabServerClient.exe
echo.
echo To run:
echo 1. Admin: dotnet run --project LabServerAdmin\LabServerAdmin.csproj
echo 2. Client: dotnet run --project LabServerClient\LabServerClient.csproj
echo.
pause
