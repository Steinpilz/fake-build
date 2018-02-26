@echo off
cls

if not exist "packages" (
	.paket\paket.exe restore
	if errorlevel 1 (
	exit /b %errorlevel%
	)
)

"packages\FAKE\tools\Fake.exe" scripts\build.fsx %*
