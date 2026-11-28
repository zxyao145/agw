import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { once } from "node:events";
import { mkdir, readFile, stat, writeFile } from "node:fs/promises";
import { request } from "node:http";
import { createServer } from "node:net";
import { dirname, join, resolve } from "node:path";
import { text } from "node:stream/consumers";
import test from "node:test";
import { setTimeout as delay } from "node:timers/promises";

import type { LocalSkillPackage } from "../../shared/contracts/local-skills";
import { createScratchDirectory } from "../scratch-directory.test-helper";
import { packLocalSkill, scanLocalSkills } from "./local-skills";

type SkillResponse = { id: string; name: string; contentPath: string; sourceDigest: string | null };

async function freePort(): Promise<number> {
  const socket = createServer();
  socket.listen(0, "127.0.0.1");
  await once(socket, "listening");
  const address = socket.address();
  assert.ok(address && typeof address !== "string");
  await new Promise<void>((done) => socket.close(() => done()));
  return address.port;
}

async function apiData<T>(response: Response): Promise<T> {
  const text = await response.text();
  assert.ok(response.ok, `${response.url} -> ${response.status}: ${text}`);
  const result = JSON.parse(text) as { code: number; data: T };
  assert.equal(result.code, 0, text);
  return result.data;
}

async function authorize(url: string, password: string): Promise<string> {
  const cookies = new Map<string, string>();
  let csrf: string | undefined;
  async function accountRequest<T>(path: string, body?: object): Promise<T> {
    const headers = new Headers({ cookie: [...cookies.values()].join("; ") });
    if (csrf) headers.set("X-CSRF-TOKEN", csrf);
    if (body) headers.set("Content-Type", "application/json");
    const response = await fetch(`${url}/api/auth/${path}`, {
      method: body ? "POST" : "GET",
      headers,
      body: body && JSON.stringify(body),
    });
    for (const cookie of response.headers.getSetCookie()) {
      const pair = cookie.split(";", 1)[0];
      cookies.set(pair.slice(0, pair.indexOf("=")), pair);
    }
    return apiData<T>(response);
  }
  csrf = (await accountRequest<{ requestToken: string }>("antiforgery")).requestToken;
  await accountRequest("login", { password });
  csrf = (await accountRequest<{ requestToken: string }>("antiforgery")).requestToken;
  return (await accountRequest<{ token: string }>("tokens", { name: "local-skills-integration" }))
    .token;
}

test("localSkills_ImportUpdateAndLimits_MatchServer", { timeout: 180_000 }, async (t) => {
  const port = await freePort();
  const scratch = await createScratchDirectory("local-skills-integration");
  const root = resolve(import.meta.dirname, "../../../../../..");
  const data = join(scratch.path, "data");
  const home = join(scratch.path, "home");
  const url = `http://127.0.0.1:${port}`;
  const password = randomBytes(18).toString("hex");
  await mkdir(home, { recursive: true });
  const env: NodeJS.ProcessEnv = { ...process.env, HOME: home, USERPROFILE: home };
  delete env.ASPNETCORE_ENVIRONMENT;
  delete env.ASPNETCORE_URLS;
  const server = spawn(
    "dotnet",
    [
      resolve(root, "src/server/Agw.Standalone.Host/bin/Debug/net10.0/agw-server.dll"),
      "--environment=Production",
      `--urls=${url}`,
      `--AgwDataDir=${data}`,
      `--AgwLogDir=${join(scratch.path, "logs")}`,
      "--Database:Provider=sqlite",
      "--Database:ConnectionString=",
      "--Execution:Provider=InProcess",
      "--DistributedLock:Provider=inmemory",
      `--Setup:AdminPassword=${password}`,
    ],
    { cwd: root, env, stdio: ["ignore", "pipe", "pipe"] },
  );
  const closed = once(server, "close");
  t.after(async () => {
    if (server.exitCode === null && server.signalCode === null) server.kill("SIGTERM");
    try {
      await closed;
    } finally {
      await scratch.dispose();
    }
  });
  let output = "";
  server.stdout.on("data", (chunk) => (output += chunk.toString()));
  server.stderr.on("data", (chunk) => (output += chunk.toString()));
  const startup = AbortSignal.timeout(30_000);
  let initialized = false;
  while (!startup.aborted && server.exitCode === null) {
    try {
      const response = await fetch(`${url}/api/server-info`, { signal: startup });
      if (response.ok && (await apiData<{ initialized: boolean }>(response)).initialized) {
        initialized = true;
        break;
      }
    } catch (error) {
      if (!(error instanceof TypeError) && !startup.aborted) throw error;
    }
    await delay(100);
  }
  assert.ok(initialized, output);
  const token = await authorize(url, password);
  const headers = { authorization: `Bearer ${token}` };

  async function writeSkillFile(directory: string, path: string, contents: string | Buffer) {
    const file = join(home, ".agents", "skills", directory, path);
    await mkdir(dirname(file), { recursive: true });
    await writeFile(file, contents);
  }
  async function writeSkill(directory: string) {
    await writeSkillFile(
      directory,
      "SKILL.md",
      `---\nname: ${directory}\ndescription: Integration skill.\n---\nUse it.\n`,
    );
  }
  function skillForm(pkg: LocalSkillPackage, update = false): FormData {
    const body = new FormData();
    body.set("Name", pkg.name);
    body.set("Description", pkg.description);
    body.set("SourceDigest", pkg.sourceDigest);
    body.set(
      "Archive",
      new Blob([new Uint8Array(pkg.bytes)], { type: "application/zip" }),
      pkg.fileName,
    );
    if (!update) body.set("Kind", "Local");
    return body;
  }
  async function upload(pkg: LocalSkillPackage, id?: string): Promise<Response> {
    try {
      return await fetch(`${url}/api/skills${id ? `/${id}` : ""}`, {
        method: id ? "PUT" : "POST",
        headers,
        body: skillForm(pkg, Boolean(id)),
      });
    } catch (cause) {
      throw new Error(`Upload of ${pkg.fileName} failed.\n${output}`, { cause });
    }
  }
  async function submitOversized(pkg: LocalSkillPackage, id?: string) {
    const payload = new Request(`${url}/api/skills${id ? `/${id}` : ""}`, {
      method: id ? "PUT" : "POST",
      body: skillForm(pkg, Boolean(id)),
    });
    const bytes = Buffer.from(await payload.arrayBuffer());
    const contentType = payload.headers.get("content-type");
    assert.ok(contentType);
    return new Promise<{ status: number | undefined; body: string }>((done, failed) => {
      const upload = request(payload.url, {
        method: payload.method,
        headers: {
          ...headers,
          "Content-Type": contentType,
          "Content-Length": bytes.length,
          Expect: "100-continue",
        },
        signal: AbortSignal.timeout(30_000),
      });
      // 等待服务器接受请求体，再发送 ZIP。Wait for the server to accept the request body before sending the ZIP.
      upload.once("continue", () => upload.end(bytes));
      upload.once("response", (response) => {
        void text(response).then((body) => done({ status: response.statusCode, body }), failed);
        if (!upload.writableEnded) upload.end();
      });
      upload.once("error", failed);
      upload.flushHeaders();
    });
  }
  async function readSkill(): Promise<SkillResponse> {
    const response = await fetch(`${url}/api/skills`, { headers });
    const skills = await apiData<SkillResponse[]>(response);
    const skill = skills.find(({ name }) => name === "demo");
    assert.ok(skill);
    return skill;
  }

  await writeSkill("demo");
  await writeSkillFile("demo", "scripts/run.py", "print('hello')\n");
  const resource = "示例说明\n";
  await writeSkillFile("demo", "参考/说明.md", resource);
  const [initial] = (await scanLocalSkills(home)).skills;
  assert.ok(initial?.sourceDigest);
  await apiData(await upload(await packLocalSkill(home, "demo")));
  const created = await readSkill();
  assert.equal(created.sourceDigest, initial.sourceDigest);
  const oldDirectory = join(data, created.contentPath);
  assert.equal(await readFile(join(oldDirectory, "参考/说明.md"), "utf8"), resource);

  const updatedScript = "print('updated')\n";
  await writeSkillFile("demo", "scripts/run.py", updatedScript);
  const [changed] = (await scanLocalSkills(home)).skills;
  assert.ok(changed?.sourceDigest);
  assert.notEqual(changed.sourceDigest, created.sourceDigest);
  await apiData(await upload(await packLocalSkill(home, "demo"), created.id));
  const updated = await readSkill();
  assert.equal(updated.sourceDigest, changed.sourceDigest);
  assert.equal(
    await readFile(join(data, updated.contentPath, "scripts/run.py"), "utf8"),
    updatedScript,
  );
  await assert.rejects(stat(oldDirectory), { code: "ENOENT" });
  await assert.rejects(stat(`${oldDirectory}.retired`), { code: "ENOENT" });

  await writeSkill("large");
  await writeSkillFile("large", "resource.bin", randomBytes(40 * 1024 * 1024));
  const large = await packLocalSkill(home, "large");
  assert.ok(large.bytes.length > 30_000_000);
  assert.equal(
    (await apiData<SkillResponse>(await upload(large))).sourceDigest,
    large.sourceDigest,
  );

  await writeSkill("oversized");
  await writeSkillFile("oversized", "resource.bin", randomBytes(110 * 1024 * 1024));
  const oversized = await packLocalSkill(home, "oversized");
  for (const id of [undefined, updated.id]) {
    const response = await submitOversized(oversized, id);
    assert.equal(response.status, 413, output);
    assert.equal(JSON.parse(response.body).code, 413_0001, response.body);
  }
  assert.deepEqual(await readSkill(), updated);

  const duplicate = await upload(await packLocalSkill(home, "demo"));
  assert.equal(duplicate.status, 409);
  assert.equal((await duplicate.json()).code, 409_0001);
});
