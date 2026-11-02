@echo off
rem Starts the screen server and opens the sharing page. Keep this window open while sharing.
cd /d "%~dp0"
start "" http://localhost:8765
node server.js
pause
