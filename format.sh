#!/usr/bin/env bash


# format the cs files
files=$(git diff --name-only --diff-filter=ACMR origin/main...HEAD -- '*.cs')

if [ -n "$files" ]; then
    dotnet format --verify-no-changes --include $files
fi


# format the clients
cd ./src/clients
pnpm fmt

