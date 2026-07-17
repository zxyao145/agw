import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const TRACE_TABLE_URL = new URL("./trace-table.tsx", import.meta.url);
const ROOT_LAYOUT_URL = new URL("../../../../layout.tsx", import.meta.url);
const DATE_TIME_PICKER_URL = new URL(
  "../../../../../components/date-time-picker.tsx",
  import.meta.url,
);

test("Execution traces uses the shared card surface", async () => {
  const source = await readFile(TRACE_TABLE_URL, "utf8");

  assert.match(source, /<section className="[^"]*bg-card[^"]*">/);
  assert.doesNotMatch(source, /bg-charcoal/);
});

test("date range filters use the shared shadcn date-time picker", async () => {
  const source = await readFile(TRACE_TABLE_URL, "utf8");

  assert.match(source, /import \{ DateTimePicker \} from "@\/components\/date-time-picker";/);
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

  assert.match(source, /from "@\/components\/ui\/tooltip";/);
  assert.match(source, /<Tooltip>/);
  assert.match(source, /<TooltipTrigger asChild>/);
  assert.match(source, /tabIndex=\{0\}/);
  assert.match(source, /<TooltipContent[\s\S]*?\{trace\.error\}[\s\S]*?<\/TooltipContent>/);
  assert.doesNotMatch(source, /title=\{trace\.error/);
});

test("root layout provides shadcn Tooltip context", async () => {
  const source = await readFile(ROOT_LAYOUT_URL, "utf8");

  assert.match(source, /import \{ TooltipProvider \} from "@\/components\/ui\/tooltip";/);
  assert.match(source, /<TooltipProvider>[\s\S]*?<QueryProvider>/);
  assert.match(source, /<\/QueryProvider>[\s\S]*?<\/TooltipProvider>/);
});
