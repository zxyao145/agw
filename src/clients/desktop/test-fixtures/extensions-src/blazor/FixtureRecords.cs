namespace AgwBlazorFixture;

/// <summary>本 fixture 用到的 context 字段。Context fields this fixture uses.</summary>
public sealed record FixtureContext(string ExtensionId, string StorageUri);

/// <summary>写入存储目录的内容。The content written to the storage directory.</summary>
public sealed record StorageRecord(string ExtensionId, string Nonce);
