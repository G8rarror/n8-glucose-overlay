@echo off
rem Runs tconnectsync continuously (Tandem Source -> Gluroo), basal + bolus only.
rem Reads Tandem Source over the internet; never talks to the pump. Started hidden at logon by start-hidden.vbs.
rem If tconnectsync exits (for example after a login error), it is restarted after 60 seconds.
cd /d "%~dp0"
if exist sync.log move /y sync.log sync.previous.log >nul
:loop
venv\Scripts\python.exe -m tconnectsync --auto-update --features BASAL BOLUS >> sync.log 2>&1
echo %DATE% %TIME% runner: tconnectsync exited with code %ERRORLEVEL%, restarting in 60 s >> sync.log
timeout /t 60 /nobreak >nul
goto loop
