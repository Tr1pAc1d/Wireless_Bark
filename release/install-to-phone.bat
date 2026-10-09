@echo off
setlocal
echo ========================================================
echo    Wireless Bark - Android Phone Installer
echo ========================================================
echo.

set ADB_CMD=..\adb\adb.exe
if not exist "%ADB_CMD%" set ADB_CMD=adb

echo Checking for connected Android devices via ADB...
"%ADB_CMD%" devices
echo.

echo Installing WirelessBark.apk to your phone...
"%ADB_CMD%" install -r "%~dp0WirelessBark.apk"

if %ERRORLEVEL% equ 0 (
    echo.
    echo [SUCCESS] Wireless Bark has been installed on your phone!
    echo Look for the Wireless Bark dog icon on your home screen or app drawer.
) else (
    echo.
    echo [FAILED] Installation could not complete.
    echo Please make sure:
    echo  1. Your phone is connected with a USB cable.
    echo  2. USB Debugging is ENABLED in Developer Options.
    echo  3. You accepted the "Allow USB debugging" prompt on your phone screen.
)

echo.
pause
