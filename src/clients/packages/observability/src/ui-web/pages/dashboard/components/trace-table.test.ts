import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const TRACE_TABLE_URL = new URL("./trace-table.tsx", import.meta.url);
const PACKAGES_URL = new URL("../../../../../../", import.meta.url);
const DATE_TIME_PICKER_URL = new URL("components/src/ui-web/date-time-picker.tsx", PACKAGES_URL);

test("Execution traces uses the shared card surface", async () => {
  const source = await readFile(TRACE_TABLE_URL, "utf8");

  assert.match(source, /<section className="[^"]*bg-card[^"]*">/);
  assert.doesNotMatch(source, /bg-charcoal/);
});

test("Execution traces uses conversation terminology", async () => {
  const source = await readFile(TRACE_TABLE_URL, "utf8");

  assert.match(source, /across projects and conversations\./);
  assert.match(source, /<Label htmlFor="trace-context-id">Conversation ID<\/Label>/);
  assert.match(source, /placeholder="Conversation identifier"/);
  assert.match(source, /<TableHead className="min-w-66">Conversation<\/TableHead>/);

  assert.doesNotMatch(source, /across projects and contexts\./);
  assert.doesNotMatch(source, />Context ID<\/Label>/);
  assert.doesNotMatch(source, /placeholder="Context identifier"/);
  assert.doesNotMatch(source, />Context<\/TableHead>/);
});

test("date range filters use the shared shadcn date-time picker", async () => {
  const source = await readFile(TRACE_TABLE_URL, "utf8");

  assert.match(source, /import \{ DateTimePicker \} from "@agw\/components";/);
  assert.match(
    source,
    /<Label htmlFor="trace-from-utc-date">From<\/Label>[\s\S]{0,500}<DateTimePicker[\s\S]{0,300}id="trace-from-utc"[\s\S]{0,300}clearable/,
  );
  assert.match(
    source,
    /<Label htmlFor="trace-to-utc-date">To<\/Label>[\s\S]{0,500}<DateTimePicker[\s\S]{0,300}id="trace-to-utc"[\s\S]{0,300}clearable/,
  );
  assert.doesNotMatch(source, /type="datetime-local"/);
});

test("shared date-time picker represents and clears optional empty values", async () => {
  const source = await readFile(DATE_TIME_PICKER_URL, "utf8");

  assert.match(source, /placeholder\?: string;/);
  assert.match(source, /clearable\?: boolean;/);
  assert.match(source, /parsedDateTime \? formatLocalDateTimeExact\(dateTime\) : placeholder/);
  assert.match(source, /value=\{parsedDateTime \? formatLocalTimeExact\(dateTime\) : ""\}/);
  assert.match(source, /clearable && parsedDateTime/);
  assert.match(source, /onChange\(""\)/);
});

test("shared date-time picker follows the shadcn calendar with time footer layout", async () => {
  const source = await readFile(DATE_TIME_PICKER_URL, "utf8");

  assert.match(source, /import \{ CalendarIcon, Clock2Icon \} from "lucide-react";/);
  assert.match(source, /formatLocalDateTimeExact/);
  assert.match(
    source,
    /<PopoverContent[\s\S]*?<Calendar[\s\S]*?<div className="space-y-3 border-t p-3">[\s\S]*?<Label htmlFor=\{`\$\{id\}-time`\}>Time<\/Label>[\s\S]*?type="time"[\s\S]*?<Clock2Icon[\s\S]*?<\/PopoverContent>/,
  );
  assert.match(
    source,
    /const handleDateChange[\s\S]{0,400}onChange\(replaceLocalDate\(dateTime, selectedDate\)\.toISOString\(\)\);\s*};/,
  );
  assert.doesNotMatch(source, /grid-cols-\[minmax\(0,1fr\)_8\.5rem\]/);
});

test("Input cell uses shadcn Tooltip with a single-line trigger", async () => {
  const source = await readFile(TRACE_TABLE_URL, "utf8");

  assert.match(
    source,
    /<Tooltip>[\s\S]*?<TooltipTrigger asChild>[\s\S]*?className="block max-w-64 truncate text-xs text-dust"[\s\S]*?tabIndex=\{0\}[\s\S]*?\{inputText\}[\s\S]*?<TooltipContent[\s\S]*?\{inputText\}[\s\S]*?<\/TooltipContent>/,
  );
  assert.doesNotMatch(source, /title=\{inputText\}/);
  assert.doesNotMatch(source, /whitespace-pre-line/);
});

test("Error cell uses shadcn Tooltip for complete hover and focus content", async () => {
  const source = await readFile(TRACE_TABLE_URL, "utf8");

  assert.match(source, /from "@agw\/components";/);
  assert.match(source, /<Tooltip>/);
  assert.match(source, /<TooltipTrigger asChild>/);
  assert.match(source, /tabIndex=\{0\}/);
  assert.match(source, /<TooltipContent[\s\S]*?\{trace\.error\}[\s\S]*?<\/TooltipContent>/);
  assert.doesNotMatch(source, /title=\{trace\.error/);
});
