AgwAgentFileStore copied from https://github.com/microsoft/agent-framework/blob/6f1522a50b66f117da34cc25ea299ba24a528b15/dotnet/src/Microsoft.Agents.AI/Harness/FileStore

AgwAgentFileStore 复制自上述 FileStore 目录，是文件工具与 Project Memory 共用的存储抽象；同一目录中的 AgwFileStoreEntry、AgwFileSearchResult、AgwFileSearchMatch 和 AgwFileLineEdit 位于 `Agw.Files.Abstracts.Dtos`。文件的创建、按行读取、文本替换、按行编辑、内容搜索和路径校验由 `Agw.Files` 的 `IAgwFileSystem` 与 `TextContentEditor` 提供；ProjectAgentFileStore 经由 `IAgwFileSystem` 访问 Project 目录，ProjectMemoryStore 把记忆保存在数据库中。
