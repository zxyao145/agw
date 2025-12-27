#!/bin/bash

# OpenAI Compatible API - Quick Test Script
# This script tests both /v1/chat/completions and /v1/responses endpoints

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Configuration
BASE_URL="${BASE_URL:-http://localhost:5000}"
AGENT_ID="${AGENT_ID:-}"

echo -e "${YELLOW}=== OpenAI Compatible API Test ===${NC}\n"

# Check if AGENT_ID is provided
if [ -z "$AGENT_ID" ]; then
    echo -e "${RED}Error: AGENT_ID environment variable is required${NC}"
    echo "Usage: AGENT_ID=your-agent-id-here ./test-openai-api.sh"
    echo "Example: AGENT_ID=12345678-1234-1234-1234-123456789abc ./test-openai-api.sh"
    exit 1
fi

echo "Base URL: $BASE_URL"
echo "Agent ID: $AGENT_ID"
echo ""

# Test 1: /v1/chat/completions (non-streaming)
echo -e "${YELLOW}Test 1: POST /v1/chat/completions (non-streaming)${NC}"
RESPONSE=$(curl -s -X POST "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$AGENT_ID\",
    \"messages\": [{\"role\": \"user\", \"content\": \"Hello! Please respond with just 'Hi'\"}],
    \"stream\": false
  }")

if echo "$RESPONSE" | grep -q "error"; then
    echo -e "${RED}✗ Failed${NC}"
    echo "Response: $RESPONSE"
else
    echo -e "${GREEN}✓ Success${NC}"
    echo "Response: $(echo $RESPONSE | jq -r '.choices[0].message.content' 2>/dev/null || echo $RESPONSE)"
fi
echo ""

# Test 2: /v1/responses (non-streaming)
echo -e "${YELLOW}Test 2: POST /v1/responses (non-streaming)${NC}"
RESPONSE=$(curl -s -X POST "$BASE_URL/v1/responses" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$AGENT_ID\",
    \"messages\": [{\"role\": \"user\", \"content\": \"Hello! Please respond with just 'Hi'\"}],
    \"stream\": false
  }")

if echo "$RESPONSE" | grep -q "error"; then
    echo -e "${RED}✗ Failed${NC}"
    echo "Response: $RESPONSE"
else
    echo -e "${GREEN}✓ Success${NC}"
    echo "Response: $(echo $RESPONSE | jq -r '.choices[0].message.content' 2>/dev/null || echo $RESPONSE)"
fi
echo ""

# Test 3: /v1/chat/completions (streaming)
echo -e "${YELLOW}Test 3: POST /v1/chat/completions (streaming)${NC}"
echo "Streaming response:"
curl -s -X POST "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$AGENT_ID\",
    \"messages\": [{\"role\": \"user\", \"content\": \"Count from 1 to 3\"}],
    \"stream\": true
  }" \
  --no-buffer | while IFS= read -r line; do
    if [[ $line == data:* ]]; then
        DATA="${line#data: }"
        if [[ $DATA != "[DONE]" ]]; then
            CONTENT=$(echo "$DATA" | jq -r '.choices[0].delta.content // empty' 2>/dev/null)
            if [ -n "$CONTENT" ]; then
                echo -n "$CONTENT"
            fi
        fi
    fi
done
echo -e "\n${GREEN}✓ Streaming completed${NC}\n"

# Test 4: /v1/responses (streaming)
echo -e "${YELLOW}Test 4: POST /v1/responses (streaming)${NC}"
echo "Streaming response:"
curl -s -X POST "$BASE_URL/v1/responses" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$AGENT_ID\",
    \"messages\": [{\"role\": \"user\", \"content\": \"Count from 1 to 3\"}],
    \"stream\": true
  }" \
  --no-buffer | while IFS= read -r line; do
    if [[ $line == data:* ]]; then
        DATA="${line#data: }"
        if [[ $DATA != "[DONE]" ]]; then
            CONTENT=$(echo "$DATA" | jq -r '.choices[0].delta.content // empty' 2>/dev/null)
            if [ -n "$CONTENT" ]; then
                echo -n "$CONTENT"
            fi
        fi
    fi
done
echo -e "\n${GREEN}✓ Streaming completed${NC}\n"

# Test 5: Multi-turn conversation with thread_id
echo -e "${YELLOW}Test 5: Multi-turn conversation with thread_id${NC}"
THREAD_ID="test-thread-$(date +%s)"
echo "Thread ID: $THREAD_ID"

# First message
echo "Message 1: Hello"
RESPONSE=$(curl -s -X POST "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$AGENT_ID\",
    \"messages\": [{\"role\": \"user\", \"content\": \"My name is Alice\"}],
    \"stream\": false,
    \"thread_id\": \"$THREAD_ID\"
  }")
echo "Response: $(echo $RESPONSE | jq -r '.choices[0].message.content' 2>/dev/null || echo $RESPONSE)"

# Second message (using same thread)
echo "Message 2: Do you remember my name?"
RESPONSE=$(curl -s -X POST "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$AGENT_ID\",
    \"messages\": [{\"role\": \"user\", \"content\": \"What is my name?\"}],
    \"stream\": false,
    \"thread_id\": \"$THREAD_ID\"
  }")
echo "Response: $(echo $RESPONSE | jq -r '.choices[0].message.content' 2>/dev/null || echo $RESPONSE)"
echo -e "${GREEN}✓ Multi-turn conversation test completed${NC}\n"

# Test 6: Error handling (invalid agent ID)
echo -e "${YELLOW}Test 6: Error handling (invalid agent ID)${NC}"
RESPONSE=$(curl -s -X POST "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "invalid-model-id",
    "messages": [{"role": "user", "content": "Hello"}],
    "stream": false
  }')

if echo "$RESPONSE" | grep -q "error"; then
    echo -e "${GREEN}✓ Error handling works correctly${NC}"
    echo "Error message: $(echo $RESPONSE | jq -r '.error.message' 2>/dev/null || echo $RESPONSE)"
else
    echo -e "${RED}✗ Expected error but got success${NC}"
fi
echo ""

echo -e "${GREEN}=== All tests completed ===${NC}"
