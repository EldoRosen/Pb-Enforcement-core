:: Creates folder links to the game and Torch binaries so the project can find its references.
@echo off
cd /d "%~dp0"

set /p SEDIR="Folder that contains SpaceEngineersDedicated.exe (usually ...\Torch\DedicatedServer64): "
rmdir GameBinaries >nul 2>&1
mklink /J GameBinaries "%SEDIR%"
if errorlevel 1 goto Error

set /p TORCHDIR="Folder that contains Torch.Server.exe (usually ...\Torch): "
rmdir TorchBinaries >nul 2>&1
mklink /J TorchBinaries "%TORCHDIR%"
if errorlevel 1 goto Error

echo Done! You can now build the plugin.
goto End

:Error
echo An error occurred creating the folder link. Check the folder path and try again.

:End
pause
