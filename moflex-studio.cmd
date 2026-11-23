@echo off
rem Moflex Studio from the command line: moflex-studio make "Movie (1994).mkv" --conv3d
rem   moflex-studio make --help    every option
setlocal
set "APP=%~dp0app"
set "PY=%APP%\python\python.exe"
if not exist "%PY%" set "PY=python"
"%PY%" "%APP%\moflex_studio.py" %*
