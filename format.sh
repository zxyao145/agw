#!/usr/bin/env bash


# format the cs files
files=$({
  git diff HEAD --name-only --diff-filter=ACMR -- '*.cs'
  git ls-files --others --exclude-standard -- '*.cs'
} | sort -u)

if [ -n "$files" ]; then
    printf 'C# files:\n%s\n' "$files"
    dotnet format --verify-no-changes --include $files
fi

printf '\n\n----------------------\n\n'


# format the clients
cd ./src/clients
pnpm fmt
