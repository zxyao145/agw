# OpenAI Responses API 使用指南

## 概述

D-System 现在支持两个 OpenAI 兼容的 API 端点：

1. **`/v1/chat/completions`** - 标准的 Chat Completions API (使用 `messages` 数组)
2. **`/v1/responses`** - OpenAI Responses API (使用 `input` 字段)

## API 对比

### /v1/chat/completions (Chat Completions API)

**请求格式**:
```json
{
  "model": "agent-id-guid",
  "messages": [
    {"role": "user", "content": "Hello!"}
  ],
  "stream": false,
  "previous_response_id": "optional-thread-id"
}
```

**响应格式**:
```json
{
  "id": "chatcmpl-...",
  "object": "chat.completion",
  "created": 1704067200,
  "model": "agent-id",
  "choices": [{
    "index": 0,
    "message": {
      "role": "assistant",
      "content": "Response text"
    },
    "finish_reason": "stop"
  }],
  "usage": {
    "prompt_tokens": 0,
    "completion_tokens": 0,
    "total_tokens": 0
  }
}
```

### /v1/responses (Responses API)

**请求格式**:
```json
{
  "model": "agent-id-guid",
  "input": "Hello!",
  "stream": false,
  "instructions": "You are a helpful assistant",
  "previous_response_id": "optional-thread-id"
}
```

**响应格式**:
```json
{
  "id": "resp_...",
  "object": "response",
  "created_at": 1704067200,
  "model": "agent-id",
  "status": "completed",
  "output": [{
    "index": 0,
    "type": "message",
    "message": {
      "role": "assistant",
      "content": "Response text"
    }
  }],
  "usage": {
    "input_tokens": 0,
    "output_tokens": 0,
    "total_tokens": 0
  },
  "previous_response_id": "thread-id"
}
```

## 使用示例

### 1. Chat Completions API

#### cURL (非流式)
```bash
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "12345678-1234-1234-1234-123456789abc",
    "messages": [
      {"role": "user", "content": "Tell me a joke"}
    ],
    "stream": false
  }'
```

#### cURL (流式)
```bash
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "12345678-1234-1234-1234-123456789abc",
    "messages": [
      {"role": "user", "content": "Tell me a story"}
    ],
    "stream": true
  }' \
  --no-buffer
```

#### Python (OpenAI SDK)
```python
from openai import OpenAI

client = OpenAI(
    base_url="http://localhost:5000/v1",
    api_key="not-needed"
)

# 非流式
response = client.chat.completions.create(
    model="YOUR_AGENT_ID",
    messages=[
        {"role": "user", "content": "Hello!"}
    ]
)
print(response.choices[0].message.content)

# 流式
stream = client.chat.completions.create(
    model="YOUR_AGENT_ID",
    messages=[{"role": "user", "content": "Tell me a story"}],
    stream=True
)
for chunk in stream:
    if chunk.choices[0].delta.content:
        print(chunk.choices[0].delta.content, end="")
```

### 2. Responses API

#### cURL (非流式)
```bash
curl -X POST http://localhost:5000/v1/responses \
  -H "Content-Type: application/json" \
  -d '{
    "model": "12345678-1234-1234-1234-123456789abc",
    "input": "Tell me a bedtime story about a unicorn",
    "instructions": "You are a creative storyteller",
    "stream": false
  }'
```

#### cURL (流式)
```bash
curl -X POST http://localhost:5000/v1/responses \
  -H "Content-Type: application/json" \
  -d '{
    "model": "12345678-1234-1234-1234-123456789abc",
    "input": "Tell me a story",
    "stream": true
  }' \
  --no-buffer
```

#### Python (OpenAI SDK - Responses)
```python
from openai import OpenAI

client = OpenAI(
    base_url="http://localhost:5000/v1",
    api_key="not-needed"
)

# 非流式
response = client.responses.create(
    model="YOUR_AGENT_ID",
    input="Tell me a bedtime story",
    instructions="You are a helpful assistant"
)
print(response.output[0].message.content)

# 流式
stream = client.responses.create(
    model="YOUR_AGENT_ID",
    input="Tell me a story",
    stream=True
)
for event in stream:
    if event.type == "response.output_item.content.delta":
        print(event.delta, end="")
```

#### JavaScript (fetch)
```javascript
// 非流式
const response = await fetch('http://localhost:5000/v1/responses', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    model: 'YOUR_AGENT_ID',
    input: 'Tell me a joke',
    stream: false
  })
});
const data = await response.json();
console.log(data.output[0].message.content);

// 流式
const streamResponse = await fetch('http://localhost:5000/v1/responses', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    model: 'YOUR_AGENT_ID',
    input: 'Tell me a story',
    stream: true
  })
});

const reader = streamResponse.body.getReader();
const decoder = new TextDecoder();
let buffer = '';

while (true) {
  const { done, value } = await reader.read();
  if (done) break;

  buffer += decoder.decode(value, { stream: true });
  const lines = buffer.split('\n\n');
  buffer = lines.pop() || '';

  for (const line of lines) {
    if (line.startsWith('event: response.output_item.content.delta')) {
      const dataLine = lines[lines.indexOf(line) + 1];
      if (dataLine && dataLine.startsWith('data: ')) {
        const event = JSON.parse(dataLine.substring(6));
        if (event.delta) console.log(event.delta);
      }
    }
  }
}
```

## 多轮对话

两个 API 都支持通过 `previous_response_id` 参数进行多轮对话：

### Chat Completions API
```bash
# 第一轮
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "YOUR_AGENT_ID",
    "messages": [{"role": "user", "content": "My name is Alice"}]
  }'

# 第二轮（使用返回的 thread_id）
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "YOUR_AGENT_ID",
    "messages": [{"role": "user", "content": "What is my name?"}],
    "previous_response_id": "thread-id-from-first-response"
  }'
```

### Responses API
```bash
# 第一轮
curl -X POST http://localhost:5000/v1/responses \
  -H "Content-Type: application/json" \
  -d '{
    "model": "YOUR_AGENT_ID",
    "input": "My name is Alice"
  }'

# 第二轮（使用返回的 previous_response_id）
curl -X POST http://localhost:5000/v1/responses \
  -H "Content-Type: application/json" \
  -d '{
    "model": "YOUR_AGENT_ID",
    "input": "What is my name?",
    "previous_response_id": "resp_xxx"
  }'
```

## 流式响应格式

### Chat Completions API (SSE)

```
data: {"id":"chatcmpl-xyz","object":"chat.completion.chunk","choices":[{"delta":{"role":"assistant"},...}]}

data: {"id":"chatcmpl-xyz","object":"chat.completion.chunk","choices":[{"delta":{"content":"Hello"},...}]}

data: {"id":"chatcmpl-xyz","object":"chat.completion.chunk","choices":[{"delta":{},"finish_reason":"stop"}]}

data: [DONE]
```

### Responses API (SSE with Events)

```
event: response.created
data: {"type":"response.created","response":{"id":"resp_xyz","status":"in_progress",...}}

event: response.output_item.added
data: {"type":"response.output_item.added","index":0}

event: response.output_item.content.delta
data: {"type":"response.output_item.content.delta","index":0,"delta":"Hello"}

event: response.output_item.content.delta
data: {"type":"response.output_item.content.delta","index":0,"delta":"!"}

event: response.completed
data: {"type":"response.completed","response":{"id":"resp_xyz","status":"completed",...}}

event: done
data: [DONE]
```

## 主要差异

| 特性 | Chat Completions | Responses |
|------|------------------|-----------|
| 输入格式 | `messages` 数组 | `input` 字符串/数组 |
| 系统提示 | 在 messages 中 | `instructions` 字段 |
| 响应ID格式 | `chatcmpl-xxx` | `resp_xxx` |
| 对话字段 | `thread_id` | `previous_response_id` |
| 流式事件 | 简单 SSE | 命名事件 SSE |
| 输出格式 | `choices` 数组 | `output` 数组 |
| 使用统计 | `usage.prompt_tokens` | `usage.input_tokens` |

## 高级功能

### Responses API 支持的额外参数

```json
{
  "model": "agent-id",
  "input": "Your question",
  "instructions": "System prompt",
  "stream": true,
  "temperature": 0.7,
  "max_output_tokens": 2048,
  "tools": [{
    "type": "function",
    "function": {
      "name": "get_weather",
      "description": "Get weather info",
      "parameters": {...}
    }
  }],
  "reasoning": {
    "effort": "high"
  },
  "previous_response_id": "thread-id"
}
```

## 错误处理

两个 API 都返回相同格式的错误：

```json
{
  "error": {
    "message": "Error description",
    "type": "invalid_request_error"
  }
}
```

常见错误：
- `Invalid model ID` - Model 必须是有效的 GUID (Agent ID)
- `No input/user message found` - 缺少必要的输入内容
- `Agent not found` - Agent ID 不存在或执行失败

## 推荐使用场景

### 使用 Chat Completions API 当：
- 需要与现有 OpenAI 代码兼容
- 使用标准的 OpenAI SDK
- 偏好 messages 数组格式

### 使用 Responses API 当：
- 需要简化的输入格式
- 使用 `instructions` 字段分离系统提示
- 需要更详细的流式事件
- 使用 reasoning 或高级工具功能

## 实现文件

- **控制器**: `src/backend/Agw.Api/Controllers/OpenAIController.cs`
- **Chat DTOs**: `src/backend/Agw.Api/Contracts/OpenAIContracts.cs`
- **Responses DTOs**: `src/backend/Agw.Api/Contracts/ResponsesApiContracts.cs`
- **输入类型**: `src/backend/Agw.Api/Contracts/OpenAiMessageInput.cs`
