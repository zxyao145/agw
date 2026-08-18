
@echo off

echo Running csharpier check...
dotnet csharpier check . 
if errorlevel 1 exit /b 1

cd src/clients/web 2>nul
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
