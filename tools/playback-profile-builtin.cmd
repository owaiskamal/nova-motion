@echo off
rem Laptops with two graphics chips: is playback better when the built-in one draws the picture?
rem Drop a video on this file (about 3 minutes).
set "PROFILE_ONLY=off,off-builtin,auto,auto-builtin,x2,x2-builtin"
call "%~dp0playback-profile.cmd" "%~1"
