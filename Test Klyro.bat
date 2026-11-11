@echo off
setlocal
set "ROOT=%~dp0"
pushd "%ROOT%" || goto directory_error

set "PROJECT=%ROOT%LightweightBrowser.csproj"

where dotnet >nul 2>&1
if errorlevel 1 goto dotnet_error
if not exist "%PROJECT%" goto project_error

dotnet --list-sdks 2>nul | findstr /b /c:"10." >nul
if errorlevel 1 goto sdk_error

echo Building and starting Klyro from the latest source...
echo Latest lightweight build: fixed-size tabs, slim smooth scrolling, no forced memory allocation.
dotnet run --project "%PROJECT%" --configuration Debug --no-launch-profile
set "APP_EXIT=%ERRORLEVEL%"
popd
if "%APP_EXIT%"=="0" exit /b 0
echo.
echo Klyro failed to build or start with exit code %APP_EXIT%.
pause
exit /b %APP_EXIT%

:dotnet_error
echo .NET SDK not found. Install the .NET 10 SDK to test the current source.
goto failure

:project_error
echo Source project not found: "%PROJECT%"
goto failure

:sdk_error
echo .NET 10 SDK not found. Install the .NET 10 SDK to test the current source.
goto failure

:failure
popd
pause
exit /b 1

:directory_error
echo Could not open the Klyro folder: "%ROOT%"
pause
exit /b 1
