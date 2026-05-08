# Mobile Local Config Base64URL 方案

## 背景

Agw React Native 客户端需要在本机保存访问服务端所需的两项配置：

- `serverDomain`: 服务端域名或基础 URL。
- `apiKey`: 访问服务端使用的 API key。

首次启动时，如果本地配置文件不存在，客户端展示一个多行输入框。用户粘贴 Base64URL 编码后的配置载荷，客户端解码、校验并写入本地配置文件。之后可从设置页读取和修改这两项配置。

## 本地配置文件

配置文件内容使用 UTF-8 JSON，当前版本为 `version: 1`：

```json
{
  "version": 1,
  "serverDomain": "https://api.example.com",
  "apiKey": "agw_api_key"
}
```

客户端落盘时始终写入上述规范字段。解析导入载荷时兼容 `domain` 作为 `serverDomain` 的别名，兼容 `api_key` 作为 `apiKey` 的别名。

原生端文件位置：

- iOS: `Application Support/Agw/config.json`
- Android: `filesDir/agw/config.json`

## Base64URL 生成规则

1. 构造 JSON 对象，字段为 `version`、`serverDomain`、`apiKey`。
2. 对 `serverDomain` 做规范化：
   - 必须是绝对 `http` 或 `https` URL。
   - 不允许包含用户名、密码、query 或 hash。
   - 去掉末尾多余 `/`。
3. 对 `apiKey` 做规范化：
   - 去掉首尾空白。
   - 不能为空。
4. 将规范 JSON 以 UTF-8 编码为字节序列。
5. 使用 RFC 4648 第 5 节的 URL and Filename Safe Base64 字母表编码：
   - 使用 `-` 代替标准 Base64 的 `+`。
   - 使用 `_` 代替标准 Base64 的 `/`。
   - 输出不保留 `=` padding。

示例输入 JSON：

```json
{"version":1,"serverDomain":"https://api.example.com","apiKey":"agw_api_key"}
```

示例 Base64URL：

```text
eyJ2ZXJzaW9uIjoxLCJzZXJ2ZXJEb21haW4iOiJodHRwczovL2FwaS5leGFtcGxlLmNvbSIsImFwaUtleSI6ImFnd19hcGlfa2V5In0
```

## Base64URL 解析规则

1. 去除载荷中的所有空白字符，方便复制多行文本。
2. 仅接受 Base64URL 字母表：`A-Z`、`a-z`、`0-9`、`-`、`_`。
3. 如果载荷包含标准 Base64 的 `+`、`/` 或 `=` padding，则拒绝。
4. 解码前客户端按 Base64URL 规则在内存中补齐必要 padding；如果长度或字符非法，则拒绝。
5. 将解码结果按 UTF-8 解析为 JSON。
6. 校验 JSON 必须是对象，并按本方案的本地配置文件规则规范化。
7. 校验通过后写入本地配置文件，后续设置页直接读写该配置文件。

## 错误处理

- 本地配置文件不存在：展示首次导入页。
- 导入载荷无效：保留输入内容并展示错误信息，不写文件。
- 已存在配置文件但 JSON 无效：按缺失配置处理，展示首次导入页并提示错误。
- 设置页保存失败或校验失败：保留用户输入并展示错误信息，不覆盖原文件。
