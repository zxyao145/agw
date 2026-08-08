"use client";

import * as React from "react";
import { Check, ChevronLeft, ChevronRight, MessageCircleQuestion, X } from "lucide-react";

import { Badge, Button, Checkbox, RadioGroup, RadioGroupItem, Textarea, cn } from "@agw/components";
import type { PendingHumanGate } from "../../../services/execution-hub";
import type {
  HumanInteractionQuestion,
  HumanInteractionQuestionResponse,
} from "../../../services/human-interaction";
import {
  buildQuestionResponse,
  createQuestionSelections,
  OTHER_OPTION_VALUE,
  type HumanInteractionQuestionSelections,
} from "./human-interaction-questions-state";
import MdCard from "./renders/md-card";

type HumanInteractionQuestionsProps = {
  request: PendingHumanGate & { questions: HumanInteractionQuestion[] };
  embedded?: boolean;
  onSubmit: (response: HumanInteractionQuestionResponse) => void;
  onCancel: () => void;
};

export function HumanInteractionQuestions({
  request,
  embedded = false,
  onSubmit,
  onCancel,
}: HumanInteractionQuestionsProps) {
  const [selections, setSelections] = React.useState<HumanInteractionQuestionSelections>(() =>
    createQuestionSelections(request.questions),
  );
  const [focusedOptions, setFocusedOptions] = React.useState<Record<string, string>>({});
  const [activeQuestionIndex, setActiveQuestionIndex] = React.useState(0);

  React.useEffect(() => {
    setSelections(createQuestionSelections(request.questions));
    setFocusedOptions({});
    setActiveQuestionIndex(0);
  }, [request.questions, request.requestId]);

  const response = React.useMemo(
    () => buildQuestionResponse(request.questions, selections),
    [request.questions, selections],
  );
  const questionIndex = Math.min(activeQuestionIndex, request.questions.length - 1);
  const question = request.questions[questionIndex]!;
  const selection = selections[question.question] ?? {
    selected: [],
    otherSelected: false,
    otherText: "",
  };
  const focusedLabel = focusedOptions[question.question] ?? selection.selected[0];
  const preview = question.options.find((option) => option.label === focusedLabel)?.preview;
  const singleValue = selection.otherSelected ? OTHER_OPTION_VALUE : selection.selected[0];
  const hasMultipleQuestions = request.questions.length > 1;

  const selectSingle = (question: HumanInteractionQuestion, value: string) => {
    setSelections((current) => ({
      ...current,
      [question.question]: {
        ...current[question.question]!,
        selected: value === OTHER_OPTION_VALUE ? [] : [value],
        otherSelected: value === OTHER_OPTION_VALUE,
      },
    }));
  };

  const toggleMultiple = (question: HumanInteractionQuestion, value: string, checked: boolean) => {
    setSelections((current) => {
      const selection = current[question.question]!;
      if (value === OTHER_OPTION_VALUE) {
        return {
          ...current,
          [question.question]: { ...selection, otherSelected: checked },
        };
      }

      return {
        ...current,
        [question.question]: {
          ...selection,
          selected: checked
            ? selection.selected.includes(value)
              ? selection.selected
              : [...selection.selected, value]
            : selection.selected.filter((item) => item !== value),
        },
      };
    });
  };

  const updateOtherText = (question: HumanInteractionQuestion, value: string) => {
    setSelections((current) => ({
      ...current,
      [question.question]: {
        ...current[question.question]!,
        otherText: value,
      },
    }));
  };

  return (
    <section
      className={cn(
        "pointer-events-auto rounded-xl border bg-gradient-to-br from-background via-background to-muted/35 shadow-lg",
        !embedded && "max-h-[62vh] overflow-auto agw-scrollbar",
      )}
    >
      <div
        className={cn(
          "z-10 border-b bg-background/95 px-4 py-3 backdrop-blur",
          !embedded && "sticky top-0",
        )}
      >
        <div className="flex items-start gap-3">
          <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg border bg-primary/10 text-primary shadow-sm">
            <MessageCircleQuestion className="h-[18px] w-[18px]" />
          </div>
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <h2 className="text-sm font-semibold tracking-tight">Your input is needed</h2>
              <Badge variant="secondary" className="h-5 rounded-md px-1.5 text-[10px] uppercase">
                {request.questions.length}{" "}
                {request.questions.length === 1 ? "question" : "questions"}
              </Badge>
            </div>
            <p className="mt-0.5 text-xs leading-relaxed text-muted-foreground">{request.prompt}</p>
          </div>
          {hasMultipleQuestions ? (
            <div
              className="ml-auto flex shrink-0 items-center gap-1 rounded-lg border bg-muted/35 p-1 shadow-xs"
              role="group"
              aria-label="Question navigation"
            >
              <span className="min-w-12 px-1 text-center font-mono text-[10px] tabular-nums text-muted-foreground">
                {String(questionIndex + 1).padStart(2, "0")} /{" "}
                {String(request.questions.length).padStart(2, "0")}
              </span>
              <Button
                type="button"
                variant="ghost"
                size="icon-sm"
                className="size-7 bg-background/60 shadow-xs"
                aria-label="Previous question"
                disabled={questionIndex === 0}
                onClick={() => setActiveQuestionIndex((current) => Math.max(0, current - 1))}
              >
                <ChevronLeft className="size-4" />
              </Button>
              <Button
                type="button"
                variant="ghost"
                size="icon-sm"
                className="size-7 bg-background/60 shadow-xs"
                aria-label="Next question"
                disabled={questionIndex === request.questions.length - 1}
                onClick={() =>
                  setActiveQuestionIndex((current) =>
                    Math.min(request.questions.length - 1, current + 1),
                  )
                }
              >
                <ChevronRight className="size-4" />
              </Button>
            </div>
          ) : null}
        </div>
      </div>

      <div className="space-y-5 p-4">
        <fieldset key={question.question} className="min-w-0 space-y-3">
          <legend className="w-full">
            <span className="flex items-center gap-2">
              <span className="font-mono text-[10px] tabular-nums text-muted-foreground">
                {String(questionIndex + 1).padStart(2, "0")}
              </span>
              <Badge variant="outline" className="rounded-md px-1.5 text-[10px]">
                {question.header}
              </Badge>
            </span>
            <span className="mt-1.5 block text-sm font-medium leading-snug">
              {question.question}
            </span>
            {question.multiSelect ? (
              <span className="mt-1 block text-[11px] text-muted-foreground">
                Select all that apply
              </span>
            ) : null}
          </legend>

          {question.multiSelect ? (
            <div className="grid gap-2">
              {question.options.map((option, optionIndex) => {
                const checked = selection.selected.includes(option.label);
                const id = `${request.requestId}-${questionIndex}-${optionIndex}`;
                return (
                  <label
                    key={option.label}
                    htmlFor={id}
                    className={`group flex cursor-pointer items-start gap-3 rounded-lg border px-3 py-2.5 transition-colors ${
                      checked
                        ? "border-primary/45 bg-primary/6"
                        : "border-border/80 bg-background hover:border-primary/25 hover:bg-muted/35"
                    }`}
                    onMouseEnter={() =>
                      setFocusedOptions((current) => ({
                        ...current,
                        [question.question]: option.label,
                      }))
                    }
                    onFocus={() =>
                      setFocusedOptions((current) => ({
                        ...current,
                        [question.question]: option.label,
                      }))
                    }
                  >
                    <Checkbox
                      id={id}
                      checked={checked}
                      onCheckedChange={(value) =>
                        toggleMultiple(question, option.label, value === true)
                      }
                      className="mt-0.5"
                    />
                    <OptionCopy label={option.label} description={option.description} />
                  </label>
                );
              })}
              <OtherOption
                id={`${request.requestId}-${questionIndex}-other`}
                checked={selection.otherSelected}
                multiSelect
                value={selection.otherText}
                onCheckedChange={(checked) => toggleMultiple(question, OTHER_OPTION_VALUE, checked)}
                onValueChange={(value) => updateOtherText(question, value)}
              />
            </div>
          ) : (
            <RadioGroup
              value={singleValue}
              onValueChange={(value) => selectSingle(question, value)}
              className="grid gap-2"
            >
              {question.options.map((option, optionIndex) => {
                const checked = selection.selected.includes(option.label);
                const id = `${request.requestId}-${questionIndex}-${optionIndex}`;
                return (
                  <label
                    key={option.label}
                    htmlFor={id}
                    className={`group flex cursor-pointer items-start gap-3 rounded-lg border px-3 py-2.5 transition-colors ${
                      checked
                        ? "border-primary/45 bg-primary/6"
                        : "border-border/80 bg-background hover:border-primary/25 hover:bg-muted/35"
                    }`}
                    onMouseEnter={() =>
                      setFocusedOptions((current) => ({
                        ...current,
                        [question.question]: option.label,
                      }))
                    }
                    onFocus={() =>
                      setFocusedOptions((current) => ({
                        ...current,
                        [question.question]: option.label,
                      }))
                    }
                  >
                    <RadioGroupItem id={id} value={option.label} className="mt-0.5" />
                    <OptionCopy label={option.label} description={option.description} />
                  </label>
                );
              })}
              <OtherOption
                id={`${request.requestId}-${questionIndex}-other`}
                checked={selection.otherSelected}
                multiSelect={false}
                value={selection.otherText}
                onCheckedChange={() => selectSingle(question, OTHER_OPTION_VALUE)}
                onValueChange={(value) => updateOtherText(question, value)}
              />
            </RadioGroup>
          )}

          {preview ? (
            <div className="overflow-hidden rounded-lg border border-primary/15 bg-muted/25">
              <div className="flex items-center gap-1.5 border-b border-primary/10 px-3 py-1.5 font-mono text-[10px] uppercase tracking-[0.12em] text-muted-foreground">
                <ChevronRight className="h-3 w-3 text-primary" />
                Preview
              </div>
              <div className="max-h-40 overflow-auto px-3 py-2 text-xs agw-scrollbar">
                <MdCard mdText={preview} />
              </div>
            </div>
          ) : null}
        </fieldset>
      </div>

      <div
        className={cn(
          "flex justify-end gap-2 border-t bg-background/95 px-4 py-3 backdrop-blur",
          !embedded && "sticky bottom-0",
        )}
      >
        <Button type="button" variant="outline" size="sm" onClick={onCancel}>
          <X className="h-4 w-4" />
          Cancel
        </Button>
        <Button
          type="button"
          size="sm"
          disabled={!response}
          onClick={() => response && onSubmit(response)}
        >
          <Check className="h-4 w-4" />
          Submit answers
        </Button>
      </div>
    </section>
  );
}

function OptionCopy({ label, description }: { label: string; description: string }) {
  return (
    <span className="min-w-0 flex-1">
      <span className="block text-sm font-medium leading-5">{label}</span>
      <span className="mt-0.5 block text-xs leading-relaxed text-muted-foreground">
        {description}
      </span>
    </span>
  );
}

type OtherOptionProps = {
  id: string;
  checked: boolean;
  multiSelect: boolean;
  value: string;
  onCheckedChange: (checked: boolean) => void;
  onValueChange: (value: string) => void;
};

function OtherOption({
  id,
  checked,
  multiSelect,
  value,
  onCheckedChange,
  onValueChange,
}: OtherOptionProps) {
  return (
    <div
      className={`rounded-lg border px-3 py-2.5 transition-colors ${
        checked ? "border-primary/45 bg-primary/6" : "border-border/80 bg-background"
      }`}
    >
      <label htmlFor={id} className="flex cursor-pointer items-start gap-3">
        {multiSelect ? (
          <Checkbox
            id={id}
            checked={checked}
            onCheckedChange={(next) => onCheckedChange(next === true)}
            className="mt-0.5"
          />
        ) : (
          <RadioGroupItem id={id} value={OTHER_OPTION_VALUE} className="mt-0.5" />
        )}
        <OptionCopy label="Other" description="Provide a different answer" />
      </label>
      {checked ? (
        <Textarea
          autoFocus
          value={value}
          onChange={(event) => onValueChange(event.target.value)}
          className="mt-2 min-h-16 resize-none bg-background text-sm"
          placeholder="Type your answer…"
        />
      ) : null}
    </div>
  );
}
