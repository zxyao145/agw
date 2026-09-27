AgwFileAccessProvider copied from https://github.com/microsoft/agent-framework/blob/6f1522a50b66f117da34cc25ea299ba24a528b15/dotnet/src/Microsoft.Agents.AI/Harness/FileAccess/FileAccessProvider.cs

AgwFileAccessProvider 复制自上述 FileAccessProvider，并在每个工具上增加 `directoryId` 参数，用于选择 Project 主目录或附加目录。AgwFileReadonlyAccessProvider 只暴露 AgwFileAccessProvider 中的读取工具实例，由 FileReadonlyAccessToolBlock 使用；FileAccessToolBlock 包含 FileReadonlyAccessToolBlock。
