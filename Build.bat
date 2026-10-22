:: Builds the plugin and makes Build\PB Enforcement Core.zip, ready to copy into the Torch Plugins folder.
::
::   Build.bat                    asks for the Torch folder the first time, then remembers it
::   Build.bat "D:\Servers\Torch" uses this Torch folder (and remembers it)
::   Build.bat change             asks for the Torch folder again
::   Build.bat debug              Debug build instead of Release (can be combined: Build.bat change debug)
@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "CONFIG=Release"
set "TORCH_DIR="
set "ASK=0"
set "PATH_FILE=%~dp0torch-path.txt"

:ParseArgs
if "%~1"=="" goto ArgsDone
if /i "%~1"=="debug" set "CONFIG=Debug" & shift & goto ParseArgs
if /i "%~1"=="release" set "CONFIG=Release" & shift & goto ParseArgs
if /i "%~1"=="change" set "ASK=1" & shift & goto ParseArgs
set "TORCH_DIR=%~1"
shift
goto ParseArgs
:ArgsDone

:: ---------------------------------------------------------------- Torch folder
if defined TORCH_DIR goto CheckTorch
if "%ASK%"=="1" goto AskTorch
if exist "%PATH_FILE%" set /p TORCH_DIR=<"%PATH_FILE%"
if defined TORCH_DIR goto CheckTorch

:AskTorch
echo.
echo Enter the Torch folder: the one that contains Torch.Server.exe
echo   for example  C:\Torch   or   D:\Servers\Torch
echo (you can drag the folder into this window, then press Enter)
set "TORCH_DIR="
set /p TORCH_DIR="Torch folder: "
if not defined TORCH_DIR goto AskTorch

:CheckTorch
:: Remove quotes and a trailing backslash.
set "TORCH_DIR=%TORCH_DIR:"=%"
if "%TORCH_DIR:~-1%"=="\" set "TORCH_DIR=%TORCH_DIR:~0,-1%"

if not exist "%TORCH_DIR%\Torch.dll" (
    echo.
    echo Torch.dll was not found in "%TORCH_DIR%".
    echo Pick the folder that contains Torch.Server.exe.
    goto AskTorch
)

set "GAME_DIR=%TORCH_DIR%\DedicatedServer64"
if exist "%GAME_DIR%\Sandbox.Game.dll" goto PathsOk

echo.
echo Sandbox.Game.dll was not found in "%GAME_DIR%".
echo Start Torch once so it downloads the dedicated server, or enter the folder
echo that contains SpaceEngineersDedicated.exe.

:AskGame
set "GAME_DIR="
set /p GAME_DIR="Dedicated server folder: "
if not defined GAME_DIR goto AskGame
set "GAME_DIR=%GAME_DIR:"=%"
if "%GAME_DIR:~-1%"=="\" set "GAME_DIR=%GAME_DIR:~0,-1%"
if not exist "%GAME_DIR%\Sandbox.Game.dll" (
    echo Sandbox.Game.dll was not found there either. Try again.
    goto AskGame
)

:PathsOk
> "%PATH_FILE%" echo %TORCH_DIR%
echo.
echo Torch folder:       %TORCH_DIR%
echo Dedicated server:   %GAME_DIR%

:: ---------------------------------------------------------------- folder links the project reads its references from
call :Link GameBinaries "%GAME_DIR%" || goto LinkFailed
call :Link TorchBinaries "%TORCH_DIR%" || goto LinkFailed

:: ---------------------------------------------------------------- MSBuild
set "MSBUILD="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" goto TryPath
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%i"
:TryPath
if defined MSBUILD goto Build
for /f "delims=" %%i in ('where msbuild 2^>nul') do if not defined MSBUILD set "MSBUILD=%%i"
if not defined MSBUILD goto NoMsBuild

:Build
echo MSBuild:            %MSBUILD%
echo.
echo Building PB Enforcement Core - %CONFIG% ...
echo.
:: SolutionDir puts the zip in this folder's Build\ (the trailing dot avoids a \" quoting problem).
"%MSBUILD%" "PB Enforcement Core.csproj" /t:Rebuild /p:Configuration=%CONFIG% /p:Platform=x64 /p:SolutionDir="%~dp0." /v:minimal /nologo /flp:logfile=build.log;verbosity=normal
if errorlevel 1 goto BuildFailed

echo.
echo Build succeeded.
echo Plugin package: %~dp0Build\PB Enforcement Core.zip
echo Copy it into "%TORCH_DIR%\Plugins" and restart the server.
goto End

:: ---------------------------------------------------------------- helpers and errors

:: :Link <name> <target>  makes (or remakes) a folder link called <name> that points at <target>.
:Link
rmdir "%~1" >nul 2>&1
if exist "%~1" (
    echo "%~1" is a real folder, not a link. Delete or rename it, then run this again.
    exit /b 1
)
mklink /J "%~1" "%~2" >nul
exit /b %errorlevel%

:LinkFailed
echo.
echo Could not create the GameBinaries / TorchBinaries folder links.
goto End

:NoMsBuild
echo.
echo MSBuild was not found. Install the Visual Studio Build Tools with the
echo ".NET desktop build tools" workload and the .NET Framework 4.8 targeting pack.
goto End

:BuildFailed
echo.
echo BUILD FAILED. The errors are listed above (file name and line number).
echo The full output is in "%~dp0build.log".

:End
echo.
pause
endlocal
