#!/usr/bin/env bash


# format the cs files
files=$({
  git diff HEAD --name-only --diff-filter=ACMR -- '*.cs'
  git ls-files --others --exclude-standard -- '*.cs'
} | sort -u)

if [ -n "$files" ]; then
    printf 'C# files:\n%s\n' "$files"
    # remove unused usings (IDE0005); CSharpier does not handle analyzer rules
    dotnet format style --include $files --diagnostics IDE0005 --no-restore
    # format code layout
    dotnet csharpier format $files
fi

printf '\n\n----------------------\n\n'


# format the clients
cd ./src/clients
pnpm fmt
