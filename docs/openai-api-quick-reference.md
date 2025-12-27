# OpenAI Compatible API - Quick Reference

## Endpoints

D-System 提供两个 OpenAI 兼容的 API：

```
POST /v1/chat/completions  (Chat Completions API - 标准格式)
POST /v1/responses          (Responses API - 简化格式)
```

**主要区别**:
- **Chat Completions**: 使用 `messages` 数组 (标准 OpenAI 格式)
- **Responses**: 使用 `input` 字段 (简化的 OpenAI Responses API 格式)

详细对比请参见: [Responses API 完整指南](./openai-responses-api-guide.md)

## Quick Start (Chat Completions API)

### 1. Get Your Agent ID

Visit the D-System UI at `http://localhost:5000` and copy your Agent's GUID.

### 2. Make a Request

```bash
# Chat Completions API (标准格式)
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "YOUR_AGENT_ID",
    "messages": [{"role": "user", "content": "Hello!"}]
  }'

# Responses API (简化格式)
curl -X POST http://localhost:5000/v1/responses \
  -H "Content-Type: application/json" \
  -d '{
    "model": "YOUR_AGENT_ID",
    "input": "Hello!"
  }'
```

### 3. Use with OpenAI SDK

```python
from openai import OpenAI

client = OpenAI(
    base_url="http://localhost:5000/v1",
    api_key="not-needed"
)

response = client.chat.completions.create(
    model="YOUR_AGENT_ID",
    messages=[{"role": "user", "content": "Hello!"}]
)
```

## Key Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `model` | string | ✅ | Agent ID (GUID format) |
| `messages` | array | ✅ | Chat messages |
| `stream` | boolean | ❌ | Enable streaming (default: false) |
| `thread_id` | string | ❌ | Conversation thread ID |

## Streaming Example

```bash
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "YOUR_AGENT_ID",
    "messages": [{"role": "user", "content": "Tell me a story"}],
    "stream": true
  }' \
  --no-buffer
```

## Multi-turn Conversations

```bash
# First message
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "YOUR_AGENT_ID",
    "messages": [{"role": "user", "content": "My name is Alice"}],
    "thread_id": "my-conversation"
  }'

# Second message (same thread)
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "YOUR_AGENT_ID",
    "messages": [{"role": "user", "content": "What is my name?"}],
    "thread_id": "my-conversation"
  }'
```

## Testing

```bash
# Run the test script
AGENT_ID=your-agent-id ./scripts/test-openai-api.sh

# Or manually test both endpoints
curl -X POST http://localhost:5000/v1/chat/completions -H "Content-Type: application/json" -d '{"model":"AGENT_ID","messages":[{"role":"user","content":"Hi"}]}'
curl -X POST http://localhost:5000/v1/responses -H "Content-Type: application/json" -d '{"model":"AGENT_ID","messages":[{"role":"user","content":"Hi"}]}'
```

## Implementation Files

- **Controller**: `src/backend/DSystem.Api/Controllers/OpenAIController.cs`
- **DTOs**: `src/backend/DSystem.Api/Contracts/OpenAIContracts.cs`
- **Documentation**: `docs/openai-compatible-api.md`
- **Test Script**: `scripts/test-openai-api.sh`

## Response Format

### Non-streaming

```json
{
  "id": "chatcmpl-...",
  "object": "chat.completion",
  "created": 1704067200,
  "model": "agent-id",
  "thread_id": "thread-id",
  "choices": [{
    "index": 0,
    "message": {
      "role": "assistant",
      "content": "Response text"
    },
    "finish_reason": "stop"
  }]
}
```

### Streaming

Server-Sent Events format:

```
data: {"id":"chatcmpl-...","choices":[{"delta":{"role":"assistant"},...}]}
data: {"id":"chatcmpl-...","choices":[{"delta":{"content":"Hello"},...}]}
data: {"id":"chatcmpl-...","choices":[{"delta":{"content":"!"},...}]}
data: [DONE]
```

## Error Responses

```json
{
  "error": {
    "message": "Error description",
    "type": "invalid_request_error"
  }
}
```

## Architecture

```
Request → OpenAIController
            ↓
        AgentRuntimeService
            ↓
        AIAgent (Microsoft.Agents.AI)
            ↓
        OpenAI/Provider (via configured API key)
```

## Notes

- **Model ID**: Must be a valid GUID (Agent ID from D-System)
- **Thread Management**: Threads are cached in `HybridCache`
- **Message Merging**: Automatic merging by `messageId` for chunked responses
- **System Prompt**: Configured in Agent entity, not in request
- **Token Counting**: Currently returns 0 (future enhancement)

## See Also

- [Full Documentation](../docs/openai-compatible-api.md)
- [Agent Management API](../src/backend/DSystem.Manager.Api/Controllers/AgentsController.cs)
- [Agent Runtime Service](../src/backend/DSystem.Domain/Services/AgentRuntimeService.cs)
