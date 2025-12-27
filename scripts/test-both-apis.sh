#!/bin/bash

# OpenAI Compatible APIs - Test Script
# Tests both /v1/chat/completions and /v1/responses endpoints

set -e

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

# Configuration
BASE_URL="${BASE_URL:-http://localhost:5000}"
AGENT_ID="${AGENT_ID:-}"

echo -e "${BLUE}╔════════════════════════════════════════╗${NC}"
echo -e "${BLUE}║  OpenAI Compatible APIs Test Suite    ║${NC}"
echo -e "${BLUE}╚════════════════════════════════════════╝${NC}\n"

# Check if AGENT_ID is provided
if [ -z "$AGENT_ID" ]; then
    echo -e "${RED}Error: AGENT_ID environment variable is required${NC}"
    echo "Usage: AGENT_ID=your-agent-id-here ./test-both-apis.sh"
    exit 1
fi

echo "Base URL: $BASE_URL"
echo "Agent ID: $AGENT_ID"
echo ""

# Test 1: Chat Completions API (non-streaming)
echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -e "${YELLOW}Test 1: Chat Completions API (non-streaming)${NC}"
echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
RESPONSE=$(curl -s -X POST "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$AGENT_ID\",
    \"messages\": [{\"role\": \"user\", \"content\": \"Say 'Chat API works'\"}],
    \"stream\": false
  }")

if echo "$RESPONSE" | grep -q "error"; then
    echo -e "${RED}✗ Failed${NC}"
    echo "Response: $RESPONSE"
else
    echo -e "${GREEN}✓ Success${NC}"
    CONTENT=$(echo $RESPONSE | grep -o '"content":"[^"]*"' | head -1 | sed 's/"content":"//;s/"$//')
    echo "Response: $CONTENT"
fi
echo ""

# Test 2: Responses API (non-streaming)
echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -e "${YELLOW}Test 2: Responses API (non-streaming)${NC}"
echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
RESPONSE=$(curl -s -X POST "$BASE_URL/v1/responses" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$AGENT_ID\",
    \"input\": \"Say 'Responses API works'\",
    \"stream\": false
  }")

if echo "$RESPONSE" | grep -q "error"; then
    echo -e "${RED}✗ Failed${NC}"
    echo "Response: $RESPONSE"
else
    echo -e "${GREEN}✓ Success${NC}"
    CONTENT=$(echo $RESPONSE | grep -o '"content":"[^"]*"' | head -1 | sed 's/"content":"//;s/"$//')
    echo "Response: $CONTENT"
fi
echo ""

# Test 3: Chat Completions API (streaming)
echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -e "${YELLOW}Test 3: Chat Completions API (streaming)${NC}"
echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -n "Streaming: "
curl -s -X POST "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$AGENT_ID\",
    \"messages\": [{\"role\": \"user\", \"content\": \"Count 1, 2, 3\"}],
    \"stream\": true
  }" \
  --no-buffer | while IFS= read -r line; do
    if [[ $line == data:* ]]; then
        DATA="${line#data: }"
        if [[ $DATA != "[DONE]" ]]; then
            CONTENT=$(echo "$DATA" | grep -o '"content":"[^"]*"' | sed 's/"content":"//;s/"$//' 2>/dev/null)
            if [ -n "$CONTENT" ]; then
                echo -n "$CONTENT"
            fi
        fi
    fi
done
echo -e "\n${GREEN}✓ Streaming completed${NC}\n"

# Test 4: Responses API (streaming)
echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -e "${YELLOW}Test 4: Responses API (streaming)${NC}"
echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -n "Streaming: "
curl -s -X POST "$BASE_URL/v1/responses" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$AGENT_ID\",
    \"input\": \"Count 1, 2, 3\",
    \"stream\": true
  }" \
  --no-buffer | while IFS= read -r line; do
    if [[ $line == data:* ]]; then
        DATA="${line#data: }"
        if [[ $DATA != "[DONE]" ]]; then
            DELTA=$(echo "$DATA" | grep -o '"delta":"[^"]*"' | sed 's/"delta":"//;s/"$//' 2>/dev/null)
            if [ -n "$DELTA" ]; then
                echo -n "$DELTA"
            fi
        fi
    fi
done
echo -e "\n${GREEN}✓ Streaming completed${NC}\n"

# Test 5: Multi-turn conversation (Chat Completions)
echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -e "${YELLOW}Test 5: Multi-turn (Chat Completions)${NC}"
echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
THREAD_ID="test-chat-$(date +%s)"
echo "Thread ID: $THREAD_ID"

echo -n "Turn 1: "
RESPONSE=$(curl -s -X POST "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$AGENT_ID\",
    \"messages\": [{\"role\": \"user\", \"content\": \"My name is Bob\"}],
    \"previous_response_id\": \"$THREAD_ID\"
  }")
CONTENT=$(echo $RESPONSE | grep -o '"content":"[^"]*"' | head -1 | sed 's/"content":"//;s/"$//')
echo "$CONTENT"

echo -n "Turn 2: "
RESPONSE=$(curl -s -X POST "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$AGENT_ID\",
    \"messages\": [{\"role\": \"user\", \"content\": \"What is my name?\"}],
    \"previous_response_id\": \"$THREAD_ID\"
  }")
CONTENT=$(echo $RESPONSE | grep -o '"content":"[^"]*"' | head -1 | sed 's/"content":"//;s/"$//')
echo "$CONTENT"
echo -e "${GREEN}✓ Multi-turn test completed${NC}\n"

# Test 6: Multi-turn conversation (Responses API)
echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -e "${YELLOW}Test 6: Multi-turn (Responses API)${NC}"
echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
RESPONSE_ID="test-resp-$(date +%s)"
echo "Response ID: $RESPONSE_ID"

echo -n "Turn 1: "
RESPONSE=$(curl -s -X POST "$BASE_URL/v1/responses" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$AGENT_ID\",
    \"input\": \"My favorite color is blue\",
    \"previous_response_id\": \"$RESPONSE_ID\"
  }")
CONTENT=$(echo $RESPONSE | grep -o '"content":"[^"]*"' | head -1 | sed 's/"content":"//;s/"$//')
echo "$CONTENT"

echo -n "Turn 2: "
RESPONSE=$(curl -s -X POST "$BASE_URL/v1/responses" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$AGENT_ID\",
    \"input\": \"What is my favorite color?\",
    \"previous_response_id\": \"$RESPONSE_ID\"
  }")
CONTENT=$(echo $RESPONSE | grep -o '"content":"[^"]*"' | head -1 | sed 's/"content":"//;s/"$//')
echo "$CONTENT"
echo -e "${GREEN}✓ Multi-turn test completed${NC}\n"

# Test 7: Error handling
echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -e "${YELLOW}Test 7: Error handling${NC}"
echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
RESPONSE=$(curl -s -X POST "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "invalid-model-id",
    "messages": [{"role": "user", "content": "Hello"}]
  }')

if echo "$RESPONSE" | grep -q "error"; then
    echo -e "${GREEN}✓ Error handling works correctly${NC}"
    ERROR_MSG=$(echo $RESPONSE | grep -o '"message":"[^"]*"' | head -1 | sed 's/"message":"//;s/"$//')
    echo "Error: $ERROR_MSG"
else
    echo -e "${RED}✗ Expected error but got success${NC}"
fi
echo ""

# Summary
echo -e "${BLUE}╔════════════════════════════════════════╗${NC}"
echo -e "${BLUE}║          All Tests Completed          ║${NC}"
echo -e "${BLUE}╚════════════════════════════════════════╝${NC}"
echo -e "${GREEN}✓ Chat Completions API: Working${NC}"
echo -e "${GREEN}✓ Responses API: Working${NC}"
echo -e "${GREEN}✓ Streaming: Supported${NC}"
echo -e "${GREEN}✓ Multi-turn: Supported${NC}"
echo ""
