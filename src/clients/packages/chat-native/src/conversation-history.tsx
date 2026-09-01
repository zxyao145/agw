import * as React from "react";
import { Image as ExpoImage } from "expo-image";
import {
  Check,
  ChevronDown,
  ChevronRight,
  CircleHelp,
  Flag,
  Link as LinkIcon,
  RotateCcw,
  ShieldCheck,
  Wrench,
  X,
} from "lucide-react-native";
import {
  ActivityIndicator,
  FlatList,
  Linking,
  Pressable,
  StyleSheet,
  Text,
  TextInput,
  View,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
} from "react-native";
import { EnrichedMarkdownText, type MarkdownStyle } from "react-native-enriched-markdown";

import {
  buildQuestionResponse,
  buildConversationRenderModel,
  createAutoScrollState,
  createQuestionSelections,
  normalizeMathDelimiters,
  updateAutoScrollState,
  type ConversationRenderItem,
  type AgentflowCheckpointAvailability,
  type HumanInteractionQuestion,
  type HumanInteractionQuestionSelections,
  type PendingHumanGate,
  type PresentedContent,
  type PresentedMessage,
} from "@agw/chat-core";
import type { PermissionMode } from "@agw/execution-core";

import { padInlineCode } from "./native-markdown";
import { defaultNativeChatTheme, type NativeChatTheme } from "./theme";

export type NativeHumanResponse = {
  approved: boolean;
  responseText?: string;
  approvalScope?: "once" | "always-tool" | "always-arguments";
  responseData?: unknown;
};

export type NativeConversationHistoryHandle = {
  scrollToBottom(): void;
  scrollToTop(): void;
};

export type NativeConversationHistoryProps = {
  items: ConversationRenderItem[];
  loading?: boolean;
  reconnecting?: boolean;
  error?: string | null;
  permissionMode?: PermissionMode;
  showCheckpointResume?: boolean;
  checkpointResumeDisabled?: boolean;
  onCheckpointResume?: (occurrenceId: string) => void;
  onHumanResponse?: (response: NativeHumanResponse) => void;
  theme?: NativeChatTheme;
};

export type NativeConversationHistoryHostProps = Omit<NativeConversationHistoryProps, "items"> & {
  messages: import("@agw/api").AiMessage[];
  pendingHumanGate?: PendingHumanGate | null;
  checkpointAvailability?: AgentflowCheckpointAvailability[];
};

export const NativeConversationHistoryHost = React.forwardRef<
  NativeConversationHistoryHandle,
  NativeConversationHistoryHostProps
>(function NativeConversationHistoryHost(
  { messages, pendingHumanGate, checkpointAvailability, ...props },
  ref,
) {
  const items = React.useMemo(
    () =>
      buildConversationRenderModel(messages, {
        pendingHumanGate,
        checkpointAvailability,
        collapseToolRuns: false,
      }),
    [checkpointAvailability, messages, pendingHumanGate],
  );
  return <NativeConversationHistory ref={ref} items={items} {...props} />;
});

export const NativeConversationHistory = React.forwardRef<
  NativeConversationHistoryHandle,
  NativeConversationHistoryProps
>(function NativeConversationHistory(
  {
    items,
    loading = false,
    reconnecting = false,
    error = null,
    permissionMode,
    showCheckpointResume = false,
    checkpointResumeDisabled = false,
    onCheckpointResume,
    onHumanResponse,
    theme = defaultNativeChatTheme,
  },
  ref,
) {
  const listRef = React.useRef<FlatList<ConversationRenderItem>>(null);
  const autoScrollRef = React.useRef(createAutoScrollState());
  const metricsRef = React.useRef({ clientHeight: 0, scrollHeight: 0, scrollTop: 0 });
  const styles = React.useMemo(() => createStyles(theme), [theme]);

  React.useImperativeHandle(
    ref,
    () => ({
      scrollToBottom: () => {
        autoScrollRef.current = {
          ...autoScrollRef.current,
          shouldAutoScroll: true,
        };
        listRef.current?.scrollToEnd({ animated: true });
      },
      scrollToTop: () => listRef.current?.scrollToOffset({ offset: 0, animated: true }),
    }),
    [],
  );

  React.useEffect(() => {
    autoScrollRef.current = createAutoScrollState();
  }, [items[0]?.key]);

  const handleScroll = (event: NativeSyntheticEvent<NativeScrollEvent>) => {
    const { contentOffset, contentSize, layoutMeasurement } = event.nativeEvent;
    metricsRef.current = {
      clientHeight: layoutMeasurement.height,
      scrollHeight: contentSize.height,
      scrollTop: contentOffset.y,
    };
    autoScrollRef.current = updateAutoScrollState(autoScrollRef.current, metricsRef.current);
  };

  const handleContentSizeChange = (_width: number, height: number) => {
    metricsRef.current = { ...metricsRef.current, scrollHeight: height };
    if (autoScrollRef.current.shouldAutoScroll && items.length > 0) {
      requestAnimationFrame(() => listRef.current?.scrollToEnd({ animated: false }));
    }
  };

  if (loading) {
    return (
      <View style={styles.center}>
        <ActivityIndicator color={theme.primary} />
      </View>
    );
  }

  return (
    <>
      {items.length === 0 ? (
        <View style={styles.empty}>
          <Text style={styles.emptyTitle}>Start a conversation</Text>
          <Text style={styles.emptyText}>
            Choose an agent, then ask Agw to work with the selected project.
          </Text>
        </View>
      ) : (
        <FlatList
          ref={listRef}
          data={items}
          keyExtractor={(item) => item.key}
          contentContainerStyle={styles.list}
          keyboardDismissMode="interactive"
          keyboardShouldPersistTaps="handled"
          onScroll={handleScroll}
          onContentSizeChange={handleContentSizeChange}
          scrollEventThrottle={16}
          renderItem={({ item, index }) => (
            <View>
              {index === 0 ? (
                <View style={styles.datePill}>
                  <Text style={styles.dateText}>Today</Text>
                </View>
              ) : null}
              <NativeRenderItem
                item={item}
                permissionMode={permissionMode}
                showCheckpointResume={showCheckpointResume}
                checkpointResumeDisabled={checkpointResumeDisabled}
                onCheckpointResume={onCheckpointResume}
                onHumanResponse={onHumanResponse}
                styles={styles}
                theme={theme}
              />
            </View>
          )}
        />
      )}
      {reconnecting ? <Text style={styles.status}>Reconnecting to the execution…</Text> : null}
      {error ? <Text style={styles.error}>{error}</Text> : null}
    </>
  );
});

function NativeRenderItem({
  item,
  permissionMode,
  showCheckpointResume,
  checkpointResumeDisabled,
  onCheckpointResume,
  onHumanResponse,
  styles,
  theme,
}: {
  item: ConversationRenderItem;
  permissionMode?: PermissionMode;
  showCheckpointResume: boolean;
  checkpointResumeDisabled: boolean;
  onCheckpointResume?: (occurrenceId: string) => void;
  onHumanResponse?: (response: NativeHumanResponse) => void;
  styles: ReturnType<typeof createStyles>;
  theme: NativeChatTheme;
}) {
  if (item.type === "tool-accordion") {
    return <NativeToolAccordion item={item} styles={styles} theme={theme} />;
  }
  if (item.type === "tool-batch") {
    return (
      <View>
        {item.tools.map((tool) => (
          <NativeToolAccordion
            key={tool.identity}
            item={{
              ...tool,
              type: "tool-accordion",
              key: tool.identity,
              alignment: "left",
              width: "normal",
            }}
            styles={styles}
            theme={theme}
          />
        ))}
      </View>
    );
  }
  if (item.type === "human-interaction-result") {
    return <NativeQuestionResult result={item.result} styles={styles} theme={theme} />;
  }
  if (item.type === "human-interaction") {
    return (
      <NativeHumanInteraction
        request={item.request}
        permissionMode={permissionMode}
        onResponse={onHumanResponse}
        styles={styles}
        theme={theme}
      />
    );
  }
  if (item.type === "tool-state") {
    return <NativeToolState item={item} styles={styles} theme={theme} />;
  }
  if (item.type === "checkpoint") {
    const canResume = item.availability?.available === true && !checkpointResumeDisabled;
    return (
      <View style={styles.specialCard}>
        <View style={styles.specialHeader}>
          <Flag color={theme.primary} size={18} />
          <View style={styles.specialCopy}>
            <Text style={styles.specialTitle}>{item.checkpoint.name}</Text>
            <Text style={styles.specialDescription}>
              Snapshot saved. The workflow continued automatically.
            </Text>
          </View>
          {showCheckpointResume ? (
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={`Resume from ${item.checkpoint.name}`}
              disabled={!canResume || !onCheckpointResume}
              onPress={() => onCheckpointResume?.(item.checkpoint.occurrenceId)}
              style={[styles.smallButton, (!canResume || !onCheckpointResume) && styles.disabled]}
            >
              <RotateCcw color={theme.ink} size={15} />
              <Text style={styles.smallButtonText}>Resume</Text>
            </Pressable>
          ) : null}
        </View>
      </View>
    );
  }
  const message = (
    <NativeMessage
      message={item.message}
      result={item.type === "result"}
      styles={styles}
      theme={theme}
    />
  );
  return item.type === "result" ? <View style={styles.resultSection}>{message}</View> : message;
}

function NativeToolState({
  item,
  styles,
  theme,
}: {
  item: Extract<ConversationRenderItem, { type: "tool-state" }>;
  styles: ReturnType<typeof createStyles>;
  theme: NativeChatTheme;
}) {
  const values = item.message.additionalProperties ?? {};
  if (item.stateType === "todo") {
    const todos = Array.isArray(values.items)
      ? (values.items as Array<{ id?: string; title?: string; isComplete?: boolean }>)
      : [];
    return (
      <View style={styles.specialCard}>
        <Text style={styles.specialTitle}>
          Todo · {todos.filter((todo) => todo.isComplete).length}/{todos.length}
        </Text>
        {todos.map((todo, index) => (
          <Text
            key={todo.id ?? String(index)}
            style={[styles.specialDescription, todo.isComplete && styles.completedText]}
          >
            {todo.isComplete ? "✓" : "○"} {todo.title ?? "Todo"}
          </Text>
        ))}
      </View>
    );
  }
  if (item.stateType === "background") {
    const tasks = Array.isArray(values.tasks) ? values.tasks : [];
    return (
      <View style={styles.specialCard}>
        <Text style={styles.specialTitle}>Background tasks</Text>
        <Text style={styles.specialDescription}>{tasks.length} running</Text>
      </View>
    );
  }
  if (item.stateType === "warning") {
    const warning = item.message.contents.find(
      (content) => typeof content.content === "string",
    )?.content;
    return (
      <View style={[styles.specialCard, styles.warningCard]}>
        <Text style={styles.warningText}>
          {typeof warning === "string" ? warning : "A Tool used a fallback."}
        </Text>
      </View>
    );
  }
  return (
    <View style={styles.specialCard}>
      <Text style={styles.specialTitle}>Agent mode</Text>
      <Text style={styles.specialDescription}>
        {typeof values.mode === "string" ? values.mode : "unknown"}
      </Text>
    </View>
  );
}

function NativeMessage({
  message,
  result = false,
  compact = false,
  styles,
  theme,
}: {
  message: PresentedMessage;
  result?: boolean;
  compact?: boolean;
  styles: ReturnType<typeof createStyles>;
  theme: NativeChatTheme;
}) {
  const isUser = message.alignment === "right";
  const hasPlan = message.contents.some((content) => content.type === "plan");
  const full = compact || result || hasPlan || message.width === "full";
  const meta = [message.meta?.name, message.meta?.author, message.meta?.model]
    .filter(Boolean)
    .join(" / ");
  return (
    <View style={styles.messageBlock}>
      {meta && !result ? (
        <Text style={[styles.author, isUser && styles.authorUser]}>{meta}</Text>
      ) : null}
      <View
        style={[
          styles.messageRow,
          full ? styles.messageRowFull : isUser ? styles.messageRowUser : styles.messageRowAgent,
        ]}
      >
        <View
          style={[
            styles.bubble,
            isUser ? styles.userBubble : result ? styles.resultBubble : styles.agentBubble,
            full && styles.fullBubble,
            compact && styles.compactBubble,
          ]}
        >
          {result ? (
            <View style={styles.resultHeading}>
              <Text style={styles.resultTitle}>Result</Text>
            </View>
          ) : null}
          {message.contents.map((content, index) => (
            <NativeContent
              key={`${content.type}:${index}`}
              content={content}
              styles={styles}
              theme={theme}
            />
          ))}
        </View>
      </View>
    </View>
  );
}

function NativeContent({
  content,
  styles,
  theme,
}: {
  content: PresentedContent;
  styles: ReturnType<typeof createStyles>;
  theme: NativeChatTheme;
}) {
  if (content.type === "image") {
    return (
      <ExpoImage source={{ uri: content.uri }} contentFit="contain" style={styles.messageImage} />
    );
  }
  if (content.type === "uri") {
    return (
      <Pressable onPress={() => void Linking.openURL(content.uri)} style={styles.linkRow}>
        <LinkIcon color={theme.primary} size={15} />
        <Text numberOfLines={2} style={styles.linkText}>
          {content.name || content.uri}
        </Text>
      </Pressable>
    );
  }
  if (content.type === "error") return <Text style={styles.contentError}>{content.text}</Text>;
  if (content.type === "plain") return <Text style={styles.plainText}>{content.text}</Text>;
  if (content.type === "reasoning") {
    return (
      <NativeCollapsibleMarkdown
        value={content.markdown}
        preview={content.preview}
        styles={styles}
        theme={theme}
      />
    );
  }
  if (content.type === "plan") {
    return (
      <View>
        {content.leadingMarkdown ? (
          <NativeMarkdown value={content.leadingMarkdown} theme={theme} />
        ) : null}
        <View style={styles.planCard}>
          <Text style={styles.planTitle}>Plan</Text>
          <NativeMarkdown value={content.markdown} theme={theme} />
        </View>
        {content.trailingMarkdown ? (
          <NativeMarkdown value={content.trailingMarkdown} theme={theme} />
        ) : null}
      </View>
    );
  }
  return <NativeMarkdown value={content.markdown} theme={theme} />;
}

function NativeMarkdown({ value, theme }: { value: string; theme: NativeChatTheme }) {
  const markdown = React.useMemo(() => padInlineCode(normalizeMathDelimiters(value)), [value]);
  const markdownStyle = React.useMemo<MarkdownStyle>(
    () => ({
      paragraph: {
        color: theme.ink,
        fontFamily: theme.fontRegular,
        fontSize: 14,
        lineHeight: 21,
        marginBottom: 7,
      },
      h1: { color: theme.ink, fontFamily: theme.fontSemibold, fontSize: 20 },
      h2: { color: theme.ink, fontFamily: theme.fontSemibold, fontSize: 18 },
      h3: { color: theme.ink, fontFamily: theme.fontSemibold, fontSize: 16 },
      strong: { color: theme.ink, fontFamily: theme.fontSemibold, fontWeight: "normal" },
      link: { color: theme.primary, underline: true },
      code: { color: theme.ink, backgroundColor: theme.code, borderColor: "transparent" },
      codeBlock: {
        color: theme.ink,
        backgroundColor: theme.code,
        borderColor: "transparent",
        borderWidth: 0,
        borderRadius: 9,
        padding: 10,
      },
      table: {
        color: theme.ink,
        borderColor: theme.border,
        borderWidth: 1,
        borderRadius: 8,
        headerBackgroundColor: theme.code,
      },
      math: { color: theme.ink, backgroundColor: theme.code, padding: 8 },
      inlineMath: { color: theme.ink },
    }),
    [theme],
  );
  return (
    <EnrichedMarkdownText
      markdown={markdown}
      flavor="github"
      markdownStyle={markdownStyle}
      allowTrailingMargin={false}
      onLinkPress={({ url }) => void Linking.openURL(url)}
    />
  );
}

function NativeCollapsibleMarkdown({
  value,
  preview,
  styles,
  theme,
}: {
  value: string;
  preview: string;
  styles: ReturnType<typeof createStyles>;
  theme: NativeChatTheme;
}) {
  const [open, setOpen] = React.useState(false);
  return (
    <Pressable
      accessibilityRole="button"
      onPress={() => setOpen((current) => !current)}
      style={styles.reasoningHeader}
    >
      {open ? (
        <ChevronDown color={theme.muted} size={15} />
      ) : (
        <ChevronRight color={theme.muted} size={15} />
      )}
      <View style={styles.collapsibleContent}>
        <NativeMarkdown value={open ? value : preview} theme={theme} />
      </View>
    </Pressable>
  );
}

function NativeToolAccordion({
  item,
  styles,
  theme,
}: {
  item: Extract<ConversationRenderItem, { type: "tool-accordion" }>;
  styles: ReturnType<typeof createStyles>;
  theme: NativeChatTheme;
}) {
  const [open, setOpen] = React.useState(false);
  return (
    <View style={styles.toolCard}>
      <Pressable
        accessibilityRole="button"
        accessibilityState={{ expanded: open }}
        onPress={() => setOpen((current) => !current)}
        style={styles.toolHeader}
      >
        <Wrench color={theme.primary} size={18} />
        <Text numberOfLines={1} style={styles.toolTitle}>
          {item.toolName}
        </Text>
        {open ? (
          <ChevronDown color={theme.muted} size={18} />
        ) : (
          <ChevronRight color={theme.muted} size={18} />
        )}
      </Pressable>
      {open
        ? item.messages.map((message) => (
            <View key={message.identity} style={styles.toolBody}>
              <NativeMessage message={message} compact styles={styles} theme={theme} />
            </View>
          ))
        : null}
    </View>
  );
}

function NativeQuestionResult({
  result,
  styles,
  theme,
}: {
  result: Extract<ConversationRenderItem, { type: "human-interaction-result" }>["result"];
  styles: ReturnType<typeof createStyles>;
  theme: NativeChatTheme;
}) {
  const [open, setOpen] = React.useState(true);
  return (
    <View style={styles.questionResult}>
      <Pressable onPress={() => setOpen((current) => !current)} style={styles.questionResultHeader}>
        {open ? (
          <ChevronDown color={theme.muted} size={16} />
        ) : (
          <ChevronRight color={theme.muted} size={16} />
        )}
        <CircleHelp color={theme.muted} size={15} />
        <Text style={styles.questionResultTitle}>
          Asked {result.items.length} {result.items.length === 1 ? "question" : "questions"}
        </Text>
      </Pressable>
      {open
        ? result.items.map((item) => (
            <View key={item.question} style={styles.answerRow}>
              <Text style={styles.answerQuestion}>{item.question}</Text>
              <Text style={styles.answerText}>
                {result.cancelled ? "No answer — request cancelled" : item.answer}
              </Text>
            </View>
          ))
        : null}
    </View>
  );
}

function NativeHumanInteraction({
  request,
  permissionMode,
  onResponse,
  styles,
  theme,
}: {
  request: PendingHumanGate;
  permissionMode?: PermissionMode;
  onResponse?: (response: NativeHumanResponse) => void;
  styles: ReturnType<typeof createStyles>;
  theme: NativeChatTheme;
}) {
  const [responseText, setResponseText] = React.useState("");
  if (request.questions) {
    return (
      <NativeQuestions
        request={request}
        questions={request.questions}
        onResponse={onResponse}
        styles={styles}
        theme={theme}
      />
    );
  }
  if (request.modeChange) {
    const mode = request.modeChange.mode === "plan" ? "Plan" : "Execute";
    return (
      <View style={styles.interactionCard}>
        <Text style={styles.interactionTitle}>Change agent mode?</Text>
        <Text style={styles.interactionPrompt}>{request.prompt}</Text>
        <View style={styles.actions}>
          <ActionButton
            label="Cancel"
            icon={X}
            secondary
            onPress={() => onResponse?.({ approved: false })}
            styles={styles}
            theme={theme}
          />
          <ActionButton
            label={`Switch to ${mode}`}
            icon={Check}
            onPress={() => onResponse?.({ approved: true, responseData: { confirmed: true } })}
            styles={styles}
            theme={theme}
          />
        </View>
      </View>
    );
  }

  const toolApproval = request.requestType === "tool-approval";
  if (toolApproval && permissionMode === "fullAccess") return null;
  return (
    <View style={styles.interactionCard}>
      <View style={styles.specialHeader}>
        <ShieldCheck color={theme.primary} size={18} />
        <Text style={styles.interactionTitle}>
          {request.toolName || request.nodeName || "Human interaction"}
        </Text>
      </View>
      <Text style={styles.interactionPrompt}>{request.prompt}</Text>
      {request.arguments ? (
        <NativeMarkdown value={`\`\`\`json\n${request.arguments}\n\`\`\``} theme={theme} />
      ) : null}
      {request.mode.toLowerCase() === "input" ? (
        <TextInput
          value={responseText}
          onChangeText={setResponseText}
          multiline
          placeholder="Response"
          style={styles.responseInput}
        />
      ) : null}
      <View style={styles.actions}>
        <ActionButton
          label={request.mode === "input" ? "Interrupt" : "Reject"}
          icon={X}
          secondary
          onPress={() =>
            onResponse?.({ approved: false, responseText: responseText.trim() || undefined })
          }
          styles={styles}
          theme={theme}
        />
        {toolApproval && permissionMode !== "alwaysAsk" ? (
          <ActionButton
            label="Allow same arguments"
            icon={Check}
            secondary
            onPress={() => onResponse?.({ approved: true, approvalScope: "always-arguments" })}
            styles={styles}
            theme={theme}
          />
        ) : null}
        <ActionButton
          label={toolApproval ? "Allow once" : request.mode === "input" ? "Submit" : "Approve"}
          icon={Check}
          onPress={() =>
            onResponse?.({
              approved: true,
              approvalScope: "once",
              responseText: responseText.trim() || undefined,
            })
          }
          styles={styles}
          theme={theme}
        />
      </View>
    </View>
  );
}

function NativeQuestions({
  request,
  questions,
  onResponse,
  styles,
  theme,
}: {
  request: PendingHumanGate;
  questions: HumanInteractionQuestion[];
  onResponse?: (response: NativeHumanResponse) => void;
  styles: ReturnType<typeof createStyles>;
  theme: NativeChatTheme;
}) {
  const [index, setIndex] = React.useState(0);
  const [selections, setSelections] = React.useState<HumanInteractionQuestionSelections>(() =>
    createQuestionSelections(questions),
  );
  const question = questions[index]!;
  const selection = selections[question.question]!;
  const response = buildQuestionResponse(questions, selections);
  const select = (label: string) => {
    setSelections((current) => {
      const selected = current[question.question]!;
      const next = question.multiSelect
        ? selected.selected.includes(label)
          ? selected.selected.filter((item) => item !== label)
          : [...selected.selected, label]
        : [label];
      return {
        ...current,
        [question.question]: { ...selected, selected: next, otherSelected: false },
      };
    });
  };
  return (
    <View style={styles.interactionCard}>
      <Text style={styles.interactionTitle}>Your input is needed</Text>
      <Text style={styles.interactionPrompt}>{request.prompt}</Text>
      <Text style={styles.questionHeader}>{question.header}</Text>
      <Text style={styles.questionText}>{question.question}</Text>
      {question.options.map((option) => {
        const selected = selection.selected.includes(option.label);
        return (
          <Pressable
            key={option.label}
            onPress={() => select(option.label)}
            style={[styles.option, selected && styles.optionSelected]}
          >
            <View style={[styles.optionDot, selected && styles.optionDotSelected]} />
            <View style={styles.specialCopy}>
              <Text style={styles.optionTitle}>{option.label}</Text>
              <Text style={styles.optionDescription}>{option.description}</Text>
            </View>
          </Pressable>
        );
      })}
      <Pressable
        onPress={() =>
          setSelections((current) => ({
            ...current,
            [question.question]: { ...selection, selected: [], otherSelected: true },
          }))
        }
        style={[styles.option, selection.otherSelected && styles.optionSelected]}
      >
        <View style={[styles.optionDot, selection.otherSelected && styles.optionDotSelected]} />
        <Text style={styles.optionTitle}>Other</Text>
      </Pressable>
      {selection.otherSelected ? (
        <TextInput
          value={selection.otherText}
          onChangeText={(value) =>
            setSelections((current) => ({
              ...current,
              [question.question]: { ...selection, otherText: value },
            }))
          }
          placeholder="Type your answer…"
          style={styles.responseInput}
        />
      ) : null}
      <View style={styles.questionNavigation}>
        <Text style={styles.questionCount}>
          {index + 1} / {questions.length}
        </Text>
        {index > 0 ? (
          <ActionButton
            label="Previous"
            icon={ChevronRight}
            secondary
            onPress={() => setIndex((current) => current - 1)}
            styles={styles}
            theme={theme}
          />
        ) : null}
        {index < questions.length - 1 ? (
          <ActionButton
            label="Next"
            icon={ChevronRight}
            secondary
            onPress={() => setIndex((current) => current + 1)}
            styles={styles}
            theme={theme}
          />
        ) : null}
      </View>
      <View style={styles.actions}>
        <ActionButton
          label="Cancel"
          icon={X}
          secondary
          onPress={() => onResponse?.({ approved: false })}
          styles={styles}
          theme={theme}
        />
        <ActionButton
          label="Submit answers"
          icon={Check}
          disabled={!response}
          onPress={() =>
            response &&
            onResponse?.({ approved: true, approvalScope: "once", responseData: response })
          }
          styles={styles}
          theme={theme}
        />
      </View>
    </View>
  );
}

function ActionButton({
  label,
  icon: Icon,
  secondary = false,
  disabled = false,
  onPress,
  styles,
  theme,
}: {
  label: string;
  icon: typeof Check;
  secondary?: boolean;
  disabled?: boolean;
  onPress(): void;
  styles: ReturnType<typeof createStyles>;
  theme: NativeChatTheme;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      disabled={disabled}
      onPress={onPress}
      style={[
        styles.actionButton,
        secondary && styles.actionButtonSecondary,
        disabled && styles.disabled,
      ]}
    >
      <Icon color={secondary ? theme.ink : theme.white} size={15} />
      <Text style={[styles.actionButtonText, secondary && styles.actionButtonTextSecondary]}>
        {label}
      </Text>
    </Pressable>
  );
}

function createStyles(theme: NativeChatTheme) {
  return StyleSheet.create({
    center: { flex: 1, alignItems: "center", justifyContent: "center" },
    empty: { flex: 1, paddingHorizontal: 42, alignItems: "center", justifyContent: "center" },
    emptyTitle: { color: theme.ink, fontFamily: theme.fontSemibold, fontSize: 20 },
    emptyText: {
      color: theme.muted,
      fontFamily: theme.fontRegular,
      fontSize: 14,
      lineHeight: 21,
      textAlign: "center",
      marginTop: 8,
    },
    list: { paddingHorizontal: 16, paddingTop: 12, paddingBottom: 16, gap: 12 },
    datePill: {
      alignSelf: "center",
      paddingHorizontal: 12,
      paddingVertical: 5,
      borderRadius: 999,
      backgroundColor: theme.code,
      marginBottom: 4,
    },
    dateText: { color: theme.muted, fontFamily: theme.fontMedium, fontSize: 11 },
    messageBlock: { width: "100%", gap: 4 },
    messageRow: { width: "100%", flexDirection: "row" },
    messageRowAgent: { justifyContent: "flex-start" },
    messageRowUser: { justifyContent: "flex-end" },
    messageRowFull: { justifyContent: "flex-start" },
    resultSection: {
      width: "100%",
      marginTop: 20,
      paddingTop: 16,
      borderTopWidth: 1,
      borderTopColor: theme.border,
      borderStyle: "dashed",
    },
    author: { color: theme.muted, fontFamily: theme.fontMedium, fontSize: 11, marginLeft: 4 },
    authorUser: { marginLeft: 0, marginRight: 4, textAlign: "right" },
    bubble: {
      paddingHorizontal: 13,
      paddingVertical: 10,
      borderRadius: 14,
      overflow: "hidden",
      maxWidth: "88%",
      flexShrink: 1,
    },
    fullBubble: { width: "100%", maxWidth: "100%" },
    compactBubble: { paddingHorizontal: 0, paddingVertical: 0 },
    userBubble: { backgroundColor: "#f3f3f4", paddingVertical: 5 },
    agentBubble: { backgroundColor: "transparent", paddingHorizontal: 0, paddingVertical: 0 },
    resultBubble: {
      width: "100%",
      backgroundColor: theme.white,
      borderWidth: 1,
      borderColor: theme.border,
    },
    resultHeading: {
      alignSelf: "flex-start",
      marginBottom: 16,
      paddingBottom: 4,
      paddingRight: 16,
      borderBottomWidth: 1,
      borderBottomColor: theme.border,
      borderStyle: "dashed",
    },
    resultTitle: {
      color: theme.ink,
      fontFamily: theme.fontSemibold,
      fontSize: 16,
    },
    plainText: { color: theme.ink, fontFamily: theme.fontRegular, fontSize: 14, lineHeight: 21 },
    contentError: {
      color: theme.danger,
      fontFamily: theme.fontMedium,
      fontSize: 13,
      lineHeight: 19,
    },
    messageImage: {
      width: 220,
      height: 160,
      borderRadius: 10,
      backgroundColor: theme.surface,
      marginBottom: 6,
    },
    linkRow: { flexDirection: "row", alignItems: "center", gap: 7 },
    linkText: { flex: 1, color: theme.primary, fontFamily: theme.fontMedium, fontSize: 13 },
    reasoningHeader: { flexDirection: "row", alignItems: "flex-start", gap: 5 },
    collapsibleContent: { flex: 1, minWidth: 0 },
    planCard: {
      width: "100%",
      padding: 14,
      borderRadius: 16,
      borderWidth: 1,
      borderColor: theme.border,
      backgroundColor: theme.surface,
    },
    planTitle: {
      color: theme.muted,
      fontFamily: theme.fontSemibold,
      fontSize: 13,
      marginBottom: 8,
    },
    toolCard: {
      width: "100%",
      borderWidth: 1,
      borderColor: theme.border,
      borderRadius: 8,
      backgroundColor: theme.white,
      overflow: "hidden",
    },
    toolHeader: {
      minHeight: 48,
      paddingHorizontal: 12,
      paddingVertical: 12,
      flexDirection: "row",
      alignItems: "center",
      gap: 10,
    },
    toolTitle: { flex: 1, color: theme.ink, fontFamily: theme.fontSemibold, fontSize: 15 },
    toolBody: { padding: 12, borderTopWidth: StyleSheet.hairlineWidth, borderColor: theme.border },
    specialCard: {
      width: "100%",
      borderWidth: 1,
      borderColor: theme.border,
      borderRadius: 14,
      backgroundColor: theme.surface,
      padding: 13,
    },
    specialHeader: { flexDirection: "row", alignItems: "center", gap: 9 },
    specialCopy: { flex: 1, minWidth: 0 },
    specialTitle: { color: theme.ink, fontFamily: theme.fontSemibold, fontSize: 14 },
    specialDescription: {
      color: theme.muted,
      fontFamily: theme.fontRegular,
      fontSize: 12,
      marginTop: 2,
    },
    completedText: { textDecorationLine: "line-through", opacity: 0.65 },
    warningCard: { borderColor: theme.warning, backgroundColor: theme.warningSoft },
    warningText: {
      color: theme.warning,
      fontFamily: theme.fontRegular,
      fontSize: 13,
      lineHeight: 19,
    },
    smallButton: {
      minHeight: 34,
      flexDirection: "row",
      alignItems: "center",
      gap: 6,
      paddingHorizontal: 10,
      borderWidth: 1,
      borderColor: theme.border,
      borderRadius: 9,
      backgroundColor: theme.white,
    },
    smallButtonText: { color: theme.ink, fontFamily: theme.fontMedium, fontSize: 12 },
    questionResult: { width: "100%" },
    questionResultHeader: {
      flexDirection: "row",
      alignItems: "center",
      gap: 7,
      paddingVertical: 5,
    },
    questionResultTitle: { color: theme.muted, fontFamily: theme.fontMedium, fontSize: 13 },
    answerRow: { paddingLeft: 28, paddingVertical: 8 },
    answerQuestion: { color: theme.ink, fontFamily: theme.fontMedium, fontSize: 14 },
    answerText: { color: theme.muted, fontFamily: theme.fontRegular, fontSize: 14, marginTop: 3 },
    interactionCard: {
      width: "100%",
      borderWidth: 1,
      borderColor: theme.border,
      borderRadius: 14,
      backgroundColor: theme.white,
      padding: 14,
      gap: 10,
    },
    interactionTitle: { color: theme.ink, fontFamily: theme.fontSemibold, fontSize: 15 },
    interactionPrompt: {
      color: theme.muted,
      fontFamily: theme.fontRegular,
      fontSize: 13,
      lineHeight: 19,
    },
    responseInput: {
      minHeight: 54,
      borderWidth: 1,
      borderColor: theme.border,
      borderRadius: 9,
      backgroundColor: theme.white,
      color: theme.ink,
      padding: 10,
      fontFamily: theme.fontRegular,
      fontSize: 14,
    },
    actions: {
      flexDirection: "row",
      flexWrap: "wrap",
      justifyContent: "flex-end",
      gap: 8,
      marginTop: 2,
    },
    actionButton: {
      minHeight: 36,
      flexDirection: "row",
      alignItems: "center",
      gap: 6,
      paddingHorizontal: 11,
      borderRadius: 9,
      backgroundColor: theme.primary,
    },
    actionButtonSecondary: {
      backgroundColor: theme.white,
      borderWidth: 1,
      borderColor: theme.border,
    },
    actionButtonText: { color: theme.white, fontFamily: theme.fontSemibold, fontSize: 12 },
    actionButtonTextSecondary: { color: theme.ink },
    disabled: { opacity: 0.45 },
    questionHeader: {
      alignSelf: "flex-start",
      color: theme.primary,
      fontFamily: theme.fontMedium,
      fontSize: 11,
      backgroundColor: theme.code,
      borderRadius: 6,
      paddingHorizontal: 7,
      paddingVertical: 3,
    },
    questionText: {
      color: theme.ink,
      fontFamily: theme.fontSemibold,
      fontSize: 14,
      lineHeight: 20,
    },
    option: {
      flexDirection: "row",
      alignItems: "flex-start",
      gap: 10,
      borderWidth: 1,
      borderColor: theme.border,
      borderRadius: 10,
      padding: 11,
    },
    optionSelected: { borderColor: theme.primary, backgroundColor: theme.surface },
    optionDot: {
      width: 16,
      height: 16,
      borderRadius: 8,
      borderWidth: 1,
      borderColor: theme.border,
      marginTop: 2,
    },
    optionDotSelected: { borderWidth: 5, borderColor: theme.primary },
    optionTitle: { color: theme.ink, fontFamily: theme.fontMedium, fontSize: 13 },
    optionDescription: {
      color: theme.muted,
      fontFamily: theme.fontRegular,
      fontSize: 12,
      lineHeight: 17,
      marginTop: 2,
    },
    questionNavigation: {
      flexDirection: "row",
      alignItems: "center",
      justifyContent: "flex-end",
      gap: 8,
    },
    questionCount: {
      color: theme.muted,
      fontFamily: theme.fontMedium,
      fontSize: 11,
      marginRight: "auto",
    },
    status: {
      paddingHorizontal: 16,
      paddingVertical: 5,
      color: theme.warning,
      backgroundColor: theme.warningSoft,
      fontFamily: theme.fontMedium,
      fontSize: 11,
    },
    error: {
      paddingHorizontal: 16,
      paddingVertical: 6,
      color: theme.danger,
      backgroundColor: theme.dangerSoft,
      fontFamily: theme.fontRegular,
      fontSize: 11,
    },
  });
}
