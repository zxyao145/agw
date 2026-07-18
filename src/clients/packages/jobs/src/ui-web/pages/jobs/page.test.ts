import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PAGE_URL = new URL("./page.tsx", import.meta.url);
const DATE_TIME_PICKER_URL = new URL(
  "../../../../../components/src/ui-web/date-time-picker.tsx",
  import.meta.url,
);

test("job details dialog keeps header and footer fixed while details body scrolls", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /<DialogContent size="2xl" className="max-h-\[90vh\] overflow-hidden">/);
  assert.match(
    source,
    /<div className="min-h-0 max-h-\[65vh\] flex-1 space-y-6 overflow-y-auto pr-1">/,
  );
});

test("once trigger accepts only API date-time values", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /return parseApiDateTime\(value\) !== null;/);
});

test("once run time keeps the offset and uses the project date-time picker", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /Date\.now\(\) \+ 3 \* 60 \* 1000/);
  assert.match(source, /import \{ DateTimePicker \} from "@agw\/components";/);
  assert.match(source, /<DateTimePicker[\s\S]{0,240}value=\{getNextRunTimeForOnce\(form\)\}/);
  assert.doesNotMatch(source, /showPicker\(\)/);
});

test("date-time picker composes the shadcn calendar, popover, and time input", async () => {
  const source = await readFile(DATE_TIME_PICKER_URL, "utf8").catch(() => "");

  assert.match(source, /import \{ Calendar \} from "\.\/shadcn\/calendar";/);
  assert.match(source, /<Popover(?:\s|>)/);
  assert.match(source, /<Calendar/);
  assert.match(source, /type="time"/);
  assert.doesNotMatch(source, /type="datetime-local"/);
});

test("job dialog uses AgentSelector for optional agent and agentflow assignment", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /import \{ AgentSelector \} from "@agw\/chat";/);
  assert.match(
    source,
    /<AgentSelector[\s\S]{0,900}placeholder="Not assigned"[\s\S]{0,120}clearable/,
  );
  assert.match(source, /function createDefaultJobFormState\(\)[\s\S]{0,220}agentType: null/);
  assert.doesNotMatch(source, /id=\{`\$\{mode\}-agent-type`\}/);
});

test("job dialog hides optional settings behind the footer Advanced toggle", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /const \[isAdvanced, setIsAdvanced\] = React\.useState\(false\);/);
  assert.match(
    source,
    /isAdvanced \? \([\s\S]{0,3600}Job Name[\s\S]*Max Retry Count[\s\S]*Enabled/,
  );
  assert.match(source, /setIsAdvanced\(\(current\) => !current\)/);
  assert.match(source, /if \(!open\) \{[\s\S]{0,100}setIsAdvanced\(false\)/);
  assert.match(
    source,
    /isAdvanced \? "Basic" : "Advanced"[\s\S]{0,500}isSubmitting \? "Saving\.\.\." : submitLabel/,
  );
  assert.doesNotMatch(source, /<Label htmlFor=\{`\$\{mode\}-status`\}>Status<\/Label>/);
  assert.match(source, /status: job\.status/);
  assert.match(source, /status: mode === "edit" \? form\.status : undefined/);
});

test("job request allows the server to generate a blank name", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /name: form\.name\.trim\(\)/);
  assert.doesNotMatch(source, /throw new Error\("Job name is required\."\)/);
});

test("jobs table changes enabled state only after the Server succeeds", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(
    source,
    /<TableHead>Enabled<\/TableHead>[\s\S]{0,120}<TableHead>Status<\/TableHead>/,
  );
  assert.match(
    source,
    /const \[pendingEnabledJobIds, setPendingEnabledJobIds\] = React\.useState<Set<string>>/,
  );
  assert.match(source, /apiPut\(jobEnabledPath,[\s\S]{0,180}body/);
  assert.match(
    source,
    /onSuccess: \(updatedJob\)[\s\S]{0,240}queryClient\.setQueryData<JobDto\[]>\(\["jobs"\]/,
  );
  assert.match(source, /onError: \(error\)[\s\S]{0,160}toast\.error\(`Update failed:/);
  assert.match(
    source,
    /onSettled:[\s\S]{0,300}setPendingEnabledJobIds[\s\S]{0,200}delete\(variables\.jobId\)/,
  );
  assert.match(
    source,
    /<Switch[\s\S]{0,300}checked=\{job\.isEnabled\}[\s\S]{0,300}onCheckedChange=[\s\S]{0,300}disabled=\{pendingEnabledJobIds\.has\(job\.id\)\}/,
  );
  assert.doesNotMatch(source, /onMutate:/);
});
