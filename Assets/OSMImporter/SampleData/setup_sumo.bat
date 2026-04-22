@echo off
REM ============================================================
REM  Setup SUMO network from OSM file
REM  Chạy script này sau khi cài SUMO
REM ============================================================

REM Auto-detect SUMO_HOME nếu chưa set
if not defined SUMO_HOME (
    if exist "C:\Program Files (x86)\Eclipse\Sumo" (
        set "SUMO_HOME=C:\Program Files (x86)\Eclipse\Sumo"
    ) else if exist "C:\Program Files\Eclipse\Sumo" (
        set "SUMO_HOME=C:\Program Files\Eclipse\Sumo"
    ) else (
        echo ERROR: SUMO_HOME is not set and SUMO was not found in default paths.
        pause
        exit /b 1
    )
)
echo Using SUMO_HOME: %SUMO_HOME%

echo [1/3] Converting OSM to SUMO network...
"%SUMO_HOME%\bin\netconvert" --osm-files sample_hanoi.osm ^
    -o hanoi.net.xml ^
    --geometry.remove ^
    --junctions.join ^
    --tls.guess-signals ^
    --tls.default-type actuated ^
    --no-turnarounds true

if %ERRORLEVEL% NEQ 0 (
    echo ERROR: netconvert failed. Is SUMO installed and in PATH?
    pause
    exit /b 1
)

echo [2/3] Generating random trips...
python "%SUMO_HOME%\tools\randomTrips.py" ^
    -n hanoi.net.xml ^
    -o hanoi.trips.xml ^
    -e 3600 ^
    -p 2.0 ^
    --fringe-factor 5 ^
    --validate

if %ERRORLEVEL% NEQ 0 (
    echo ERROR: randomTrips.py failed. Check SUMO_HOME env variable.
    pause
    exit /b 1
)

echo [3/3] Converting trips to routes...
"%SUMO_HOME%\bin\duarouter" ^
    -n hanoi.net.xml ^
    -t hanoi.trips.xml ^
    -o hanoi.rou.xml ^
    --ignore-errors ^
    --no-warnings

echo.
echo ============================================================
echo  Done! Files created:
echo    hanoi.net.xml   - SUMO network
echo    hanoi.rou.xml   - Vehicle routes
echo    hanoi.sumocfg   - SUMO configuration (create manually or use below)
echo ============================================================
echo.

REM Tạo .sumocfg nếu chưa có
if not exist hanoi.sumocfg (
    echo Creating hanoi.sumocfg...
    (
        echo ^<configuration^>
        echo   ^<input^>
        echo     ^<net-file value="hanoi.net.xml"/^>
        echo     ^<route-files value="hanoi.rou.xml"/^>
        echo   ^</input^>
        echo   ^<time^>
        echo     ^<begin value="0"/^>
        echo     ^<end value="3600"/^>
        echo     ^<step-length value="0.05"/^>
        echo   ^</time^>
        echo   ^<processing^>
        echo     ^<lateral-resolution value="0.8"/^>
        echo   ^</processing^>
        echo ^</configuration^>
    ) > hanoi.sumocfg
    echo hanoi.sumocfg created.
)

pause
