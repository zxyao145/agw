
@echo off

echo Running dotnet format Agw.slnx check...
dotnet format Agw.slnx --verify-no-changes 
if errorlevel 1 exit /b 1

cd src\frontend\web 2>nul
if errorlevel 1 goto skip_frontend

where pnpm >nul 2>nul
if errorlevel 1 (
  echo pnpm not found
  exit /b 1
)

echo Lint...
pnpm exec oxlint ./src
if errorlevel 1 exit /b 1

echo Format...
pnpm exec oxfmt --check ./src
if errorlevel 1 exit /b 1

cd - >nul

:skip_frontend
echo Done
