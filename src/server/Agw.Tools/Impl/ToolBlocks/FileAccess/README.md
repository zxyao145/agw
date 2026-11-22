AgwFileAccessProvider copied from https://github.com/microsoft/agent-framework/blob/6f1522a50b66f117da34cc25ea299ba24a528b15/dotnet/src/Microsoft.Agents.AI/Harness/FileAccess/FileAccessProvider.cs

AgwFileAccessProvider 复制自上述 FileAccessProvider，并在每个工具上增加 `directoryId` 参数，用于选择 Project 主目录或附加目录。AgwFileReadonlyAccessProvider 只暴露 AgwFileAccessProvider 中的读取工具实例，由 FileReadonlyAccessToolBlock 使用；FileAccessToolBlock 包含 FileReadonlyAccessToolBlock。

The read tools return a string when the initial read fits on one page. For paged reads, use `content` on every page and pass `continuation` back with the same file, line range, and directory ID until there is no next cursor. A page contains at most 200 lines and 16,384 UTF-16 code units of text. In numbered output, remove any repeated line number and tab before joining fragments of a long line for `file_access_replace_lines`.

一次可以读完时，读取工具返回字符串。分页时，每页的正文在 `content` 中。使用相同的文件、行范围和目录 ID 传回 `continuation` 即可续读，最后一页没有后续位置。每页最多包含 200 行、16,384 个 UTF-16 代码单元。使用 `file_access_replace_lines` 编辑跨页长行时，先移除续页中重复的行号和制表符，再合并内容。

Pass the entire `continuation` from the previous response unchanged. The tool returns `InvalidParam` if the continuation was changed or if the opened file's version differs. Start a new read to get the current file contents.

续读时原样传回上一次响应中的整个 `continuation`。续读位置被修改，或文件版本发生变化时，工具返回 `InvalidParam`。需要读取当前文件时，从第一页重新开始。
