# OpenAI Compatible API

## Overview

D-System now provides an OpenAI-compatible API endpoint that allows seamless migration from OpenAI's Chat Completions API. The endpoint uses `AgentRuntimeService` internally to execute agents while maintaining full compatibility with OpenAI's request/response format.

## Endpoints

Both endpoints are functionally identical and can be used interchangeably:

```
POST /v1/chat/completions  (OpenAI-compatible)
POST /v1/responses          (Alternative path)
```

These mirror OpenAI's standard endpoint format, allowing existing OpenAI clients to work with minimal changes.

## Key Features

- ✅ **OpenAI-compatible format**: Drop-in replacement for OpenAI API
- ✅ **Streaming support**: Real-time responses via Server-Sent Events (SSE)
- ✅ **Thread management**: Multi-turn conversations using `thread_id`
- ✅ **Agent-based execution**: Leverages D-System's agent infrastructure
- ✅ **Message merging**: Automatic handling of chunked responses

## Request Format

### Basic Request (Non-streaming)

```json
{
  "model": "12345678-1234-1234-1234-123456789abc",
  "messages": [
    {
      "role": "user",
      "content": "Hello, how can you help me?"
    }
  ],
  "stream": false
}
```

### Streaming Request

```json
{
  "model": "12345678-1234-1234-1234-123456789abc",
  "messages": [
    {
      "role": "user",
      "content": "Tell me a story"
    }
  ],
  "stream": true
}
```

### Multi-turn Conversation

```json
{
  "model": "12345678-1234-1234-1234-123456789abc",
  "messages": [
    {
      "role": "user",
      "content": "What's the weather like?"
    }
  ],
  "stream": false,
  "thread_id": "conversation-thread-123"
}
```

## Key Differences from OpenAI

| Feature | OpenAI | D-System |
|---------|--------|----------|
| **Model ID** | String (e.g., `gpt-4`) | GUID (Agent ID) |
| **Thread ID** | N/A | Custom extension for conversation context |
| **Token Usage** | Accurate counts | Currently returns 0 (future enhancement) |
| **System Messages** | In `messages` array | Configured in Agent's SystemPrompt |

## Request Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `model` | string | ✅ | Agent ID (must be a valid GUID) |
| `messages` | array | ✅ | Conversation messages |
| `stream` | boolean | ❌ | Enable streaming (default: false) |
| `temperature` | number | ❌ | Sampling temperature (reserved for future use) |
| `max_tokens` | integer | ❌ | Max tokens to generate (reserved for future use) |
| `thread_id` | string | ❌ | Thread ID for conversation continuation |

## Response Format

### Non-streaming Response

```json
{
  "id": "chatcmpl-a1b2c3d4e5f6",
  "object": "chat.completion",
  "created": 1704067200,
  "model": "12345678-1234-1234-1234-123456789abc",
  "thread_id": "conversation-thread-123",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "Hello! I'm here to help you with..."
      },
      "finish_reason": "stop"
    }
  ],
  "usage": {
    "prompt_tokens": 0,
    "completion_tokens": 0,
    "total_tokens": 0
  }
}
```

### Streaming Response

Streams in SSE format:

```
data: {"id":"chatcmpl-xyz","object":"chat.completion.chunk","created":1704067200,"model":"...","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"chatcmpl-xyz","object":"chat.completion.chunk","created":1704067200,"model":"...","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}

data: {"id":"chatcmpl-xyz","object":"chat.completion.chunk","created":1704067200,"model":"...","choices":[{"index":0,"delta":{"content":"!"},"finish_reason":null}]}

data: {"id":"chatcmpl-xyz","object":"chat.completion.chunk","created":1704067200,"model":"...","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: [DONE]
```

## Usage Examples

### cURL

```bash
# Using /v1/chat/completions (OpenAI-compatible path)
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "12345678-1234-1234-1234-123456789abc",
    "messages": [{"role": "user", "content": "Hello!"}],
    "stream": false
  }'

# Using /v1/responses (alternative path)
curl -X POST http://localhost:5000/v1/responses \
  -H "Content-Type: application/json" \
  -d '{
    "model": "12345678-1234-1234-1234-123456789abc",
    "messages": [{"role": "user", "content": "Hello!"}],
    "stream": false
  }'

# Streaming example
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "12345678-1234-1234-1234-123456789abc",
    "messages": [{"role": "user", "content": "Hello!"}],
    "stream": true
  }' \
  --no-buffer
```

### Python (OpenAI SDK)

```python
from openai import OpenAI

# Configure client to use D-System endpoint
client = OpenAI(
    base_url="http://localhost:5000/v1",
    api_key="not-needed"  # D-System doesn't require API key in request
)

# Non-streaming
response = client.chat.completions.create(
    model="12345678-1234-1234-1234-123456789abc",  # Agent ID
    messages=[
        {"role": "user", "content": "Hello!"}
    ]
)
print(response.choices[0].message.content)

# Streaming
stream = client.chat.completions.create(
    model="12345678-1234-1234-1234-123456789abc",
    messages=[{"role": "user", "content": "Tell me a story"}],
    stream=True
)
for chunk in stream:
    if chunk.choices[0].delta.content:
        print(chunk.choices[0].delta.content, end="")
```

### JavaScript (fetch)

```javascript
// Non-streaming
const response = await fetch('http://localhost:5000/v1/chat/completions', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    model: '12345678-1234-1234-1234-123456789abc',
    messages: [{ role: 'user', content: 'Hello!' }],
    stream: false
  })
});
const data = await response.json();
console.log(data.choices[0].message.content);

// Streaming
const streamResponse = await fetch('http://localhost:5000/v1/chat/completions', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    model: '12345678-1234-1234-1234-123456789abc',
    messages: [{ role: 'user', content: 'Tell me a story' }],
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
    if (line.startsWith('data: ')) {
      const data = line.substring(6);
      if (data === '[DONE]') break;
      const chunk = JSON.parse(data);
      const content = chunk.choices[0]?.delta?.content;
      if (content) console.log(content);
    }
  }
}
```

## Migration from OpenAI

### Step 1: Update Base URL

Change your OpenAI client's base URL to point to D-System:

```python
# Before
client = OpenAI(api_key="sk-...")

# After
client = OpenAI(
    base_url="http://localhost:5000/v1",
    api_key="not-needed"
)
```

### Step 2: Replace Model Names with Agent IDs

```python
# Before
model="gpt-4"

# After
model="12345678-1234-1234-1234-123456789abc"  # Your Agent ID from D-System
```

### Step 3: (Optional) Add Thread ID for Conversations

```python
response = client.chat.completions.create(
    model="12345678-1234-1234-1234-123456789abc",
    messages=[{"role": "user", "content": "Hello!"}],
    extra_body={"thread_id": "my-conversation-123"}
)
```

## Error Handling

### Invalid Agent ID

```json
{
  "error": {
    "message": "Invalid model ID. Must be a valid GUID (Agent ID).",
    "type": "invalid_request_error"
  }
}
```

### Agent Not Found

```json
{
  "error": {
    "message": "Agent not found or execution failed.",
    "type": "invalid_request_error"
  }
}
```

### Missing User Message

```json
{
  "error": {
    "message": "No user message found in request.",
    "type": "invalid_request_error"
  }
}
```

## Implementation Details

### Architecture

```
Request → OpenAIController → AgentRuntimeService → AIAgent → OpenAI/Provider
                                      ↓
                               Thread Cache (HybridCache)
```

### Thread Management

- Threads are automatically created for new conversations
- Thread state is cached using `HybridCache` for performance
- Thread ID is returned in responses for continuation
- Threads are persisted across requests using the same `thread_id`

### Message Merging

The controller automatically merges chunked messages from the agent by `messageId`, ensuring coherent responses even when the underlying agent streams multiple chunks.

## Future Enhancements

- [ ] Token usage calculation (currently returns 0)
- [ ] Support for `temperature` and `max_tokens` parameters
- [ ] Function calling support
- [ ] Vision (image input) support
- [ ] Audio input/output support
- [ ] Batch API support

## Testing

```bash
# Start the D-System API
dotnet run --project src/backend/DSystem.Host

# Test the OpenAI-compatible endpoint
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "YOUR_AGENT_ID_HERE",
    "messages": [{"role": "user", "content": "Hello!"}],
    "stream": false
  }'

# Test the alternative /v1/responses endpoint
curl -X POST http://localhost:5000/v1/responses \
  -H "Content-Type: application/json" \
  -d '{
    "model": "YOUR_AGENT_ID_HERE",
    "messages": [{"role": "user", "content": "Hello!"}],
    "stream": false
  }'
```

## Related Documentation

- [Agent Management API](/api/agents)
- [Agent Runtime Service](/src/backend/DSystem.Domain/Services/AgentRuntimeService.cs)
- [OpenAI Chat Completions API](https://platform.openai.com/docs/api-reference/chat)
