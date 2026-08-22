import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative, resolve, sep } from "node:path";

const clientsRoot = resolve(import.meta.dirname, "..", "..");
const packagesRoot = join(clientsRoot, "packages");

function readManifest(packageDirectory) {
  return JSON.parse(readFileSync(join(packageDirectory, "package.json"), "utf8"));
}

const requiredPackages = [
  "agents",
  "api",
  "auth",
  "chat",
  "chat-core",
  "chat-native",
  "chat-runtime",
  "components",
  "http-client",
  "integrations",
  "jobs",
  "observability",
  "projects",
  "projects-core",
  "providers",
  "settings",
  "skills",
];

for (const packageDirectory of requiredPackages) {
  const manifestPath = join(packagesRoot, packageDirectory, "package.json");
  assert.ok(existsSync(manifestPath), `Missing workspace package: packages/${packageDirectory}`);
}

assert.equal(
  existsSync(join(clientsRoot, "desktop", "packages")),
  false,
  "Desktop application must not own workspace packages",
);
assert.equal(
  existsSync(join(packagesRoot, "desktop-renderer")),
  false,
  "Desktop renderer must remain inside the Desktop application, not a workspace package",
);
assert.equal(
  existsSync(join(packagesRoot, "desktop-contracts")),
  false,
  "Desktop bridge contracts must remain internal to the Desktop application",
);
assert.ok(
  existsSync(join(clientsRoot, "desktop", "renderer", "src", "app", "layout.tsx")),
  "Desktop application must own its React renderer",
);
assert.ok(
  existsSync(join(clientsRoot, "desktop", "src", "shared", "contracts", "index.ts")),
  "Desktop application is missing its internal bridge contracts",
);
assert.equal(
  existsSync(join(clientsRoot, "web", "src", "adapters", "electron")),
  false,
  "Web application must not own the Electron adapter",
);

const chatExecutionPath = join(packagesRoot, "chat", "src", "state", "execution.ts");
const desktopExecutionPath = join(
  clientsRoot,
  "desktop",
  "src",
  "shared",
  "contracts",
  "execution.ts",
);
assert.ok(existsSync(chatExecutionPath), "Chat must own its execution status model");
assert.equal(
  existsSync(desktopExecutionPath),
  false,
  "Desktop contracts must not own Chat execution status",
);

for (const packageDirectory of ["auth", "chat", "settings"]) {
  const manifest = readManifest(join(packagesRoot, packageDirectory));
  assert.equal(
    manifest.dependencies?.["@agw/desktop-contracts"],
    undefined,
    `@agw/${packageDirectory} must not depend on Desktop contracts`,
  );
  assert.equal(
    manifest.dependencies?.["@agw/desktop-renderer"],
    undefined,
    `@agw/${packageDirectory} must not depend on a Desktop renderer package`,
  );
}

const webManifest = readManifest(join(clientsRoot, "web"));
assert.equal(
  webManifest.dependencies?.["@agw/desktop-contracts"],
  undefined,
  "@agw/web must not consume Desktop bridge contracts",
);
assert.equal(
  webManifest.dependencies?.["@agw/desktop-renderer"],
  undefined,
  "@agw/web must not consume a Desktop renderer package",
);

const desktopManifest = readManifest(join(clientsRoot, "desktop"));
assert.equal(
  desktopManifest.dependencies?.["@agw/desktop-contracts"] ??
    desktopManifest.devDependencies?.["@agw/desktop-contracts"],
  undefined,
  "@agw/desktop must own bridge contracts internally",
);
assert.equal(
  desktopManifest.dependencies?.["@agw/web"] ?? desktopManifest.devDependencies?.["@agw/web"],
  undefined,
  "@agw/desktop must not depend on @agw/web",
);

const mobileRoot = join(clientsRoot, "mobile");
const mobileManifest = readManifest(mobileRoot);
assert.equal(mobileManifest.name, "@agw/mobile", "Mobile must be a pnpm workspace application");
assert.equal(
  existsSync(join(mobileRoot, "shared", "package.json")),
  false,
  "Mobile must use src/clients/mobile as its Expo root",
);
assert.equal(
  existsSync(join(mobileRoot, "package-lock.json")),
  false,
  "Mobile must use the clients pnpm lockfile",
);
for (const dependency of [
  "@agw/web",
  "@agw/desktop",
  "@agw/components",
  "@agw/chat",
  "@agw/chat-core",
  "@agw/chat-runtime",
]) {
  assert.equal(
    mobileManifest.dependencies?.[dependency] ?? mobileManifest.devDependencies?.[dependency],
    undefined,
    `@agw/mobile must not depend on ${dependency}`,
  );
}

for (const filePath of [
  ...sourceFiles(join(mobileRoot, "app")),
  ...sourceFiles(join(mobileRoot, "src")),
]) {
  const source = readFileSync(filePath, "utf8");
  const sourcePath = relative(clientsRoot, filePath);
  assert.doesNotMatch(
    source,
    /["']@agw\/(?:web|desktop|components)(?:[\/"'])/u,
    `${sourcePath} imports a Web or Desktop boundary`,
  );
  assert.doesNotMatch(source, /["']@agw\/chat["']/u, `${sourcePath} must import @agw/chat-native`);
  assert.doesNotMatch(
    source,
    /["']@agw\/projects["']/u,
    `${sourcePath} must import @agw/projects-core`,
  );
  assert.doesNotMatch(
    source,
    /(?:web|desktop)\/src/u,
    `${sourcePath} imports another application source tree`,
  );
  assert.doesNotMatch(
    source,
    /mobile\/shared|shared\/src\/rn/u,
    `${sourcePath} imports the removed Mobile architecture`,
  );
  assert.doesNotMatch(
    source,
    /\.\.\/(?:\.\.\/)*packages\//u,
    `${sourcePath} bypasses workspace package exports`,
  );
}

const forbiddenWebDirectories = ["api", "components", "features", "hooks", "lib", "types"];
for (const directory of forbiddenWebDirectories) {
  const absolutePath = join(clientsRoot, "web", "src", directory);
  assert.equal(
    existsSync(absolutePath),
    false,
    `Web application still owns shared or business code: web/src/${directory}`,
  );
}

function sourceFiles(directory) {
  if (!existsSync(directory)) return [];
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const entryPath = join(directory, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === "node_modules" || entry.name === "dist") return [];
      return sourceFiles(entryPath);
    }
    if (!entry.isFile() || !/\.(?:ts|tsx|mts|cts|js|jsx|mjs|cjs)$/u.test(entry.name)) return [];
    return [entryPath];
  });
}

for (const filePath of sourceFiles(packagesRoot)) {
  const source = readFileSync(filePath, "utf8");
  const packagePath = relative(clientsRoot, filePath);
  assert.doesNotMatch(
    source,
    /(?:from|import\s*)\s*\(?["']@\//u,
    `${packagePath} imports Web alias`,
  );
  assert.doesNotMatch(source, /["']@agw\/web(?:[\/"'])/u, `${packagePath} imports @agw/web`);
  const owningPackage = relative(packagesRoot, filePath).split(sep)[0];
  if (owningPackage !== "components" && owningPackage !== "chat-native") {
    assert.doesNotMatch(
      source,
      /["']@tanstack\/react-query["']/u,
      `${packagePath} bypasses @agw/components/query`,
    );
    assert.doesNotMatch(
      source,
      /["']@radix-ui\/react-accordion["']/u,
      `${packagePath} bypasses @agw/components Accordion primitives`,
    );
  }
  assert.doesNotMatch(
    source,
    /["']@agw\/desktop-renderer(?:[\/"'])/u,
    `${packagePath} imports the removed Desktop renderer package`,
  );
  assert.doesNotMatch(source, /web\/src/u, `${packagePath} imports web/src`);
}

for (const filePath of [
  ...sourceFiles(join(packagesRoot, "chat-core", "src")),
  ...sourceFiles(join(packagesRoot, "chat-runtime", "src")),
]) {
  const source = readFileSync(filePath, "utf8");
  const packagePath = relative(clientsRoot, filePath);
  assert.doesNotMatch(
    source,
    /["'](?:next|react-dom|react-native|expo(?:-[^"']*)?|@agw\/components)(?:[\/"'])/u,
    `${packagePath} imports a platform renderer dependency`,
  );
}

for (const filePath of sourceFiles(join(packagesRoot, "chat", "src"))) {
  const source = readFileSync(filePath, "utf8");
  const packagePath = relative(clientsRoot, filePath);
  assert.doesNotMatch(
    source,
    /["'](?:react-native|expo(?:-[^"']*)?)(?:[\/"'])/u,
    `${packagePath} imports a Native renderer dependency`,
  );
}

for (const filePath of sourceFiles(join(packagesRoot, "chat-native", "src"))) {
  const source = readFileSync(filePath, "utf8");
  const packagePath = relative(clientsRoot, filePath);
  assert.doesNotMatch(
    source,
    /["'](?:next|react-dom|@agw\/components|@agw\/chat)(?:[\/"'])/u,
    `${packagePath} imports a DOM renderer dependency`,
  );
}

for (const forbiddenImplementation of [
  join(mobileRoot, "src", "features", "chat", "message-rendering.ts"),
  join(mobileRoot, "src", "features", "chat", "image-picker.ts"),
]) {
  assert.equal(
    existsSync(forbiddenImplementation),
    false,
    `${relative(clientsRoot, forbiddenImplementation)} must live in @agw/chat-native`,
  );
}

for (const filePath of sourceFiles(join(clientsRoot, "web", "src"))) {
  const source = readFileSync(filePath, "utf8");
  const sourcePath = relative(clientsRoot, filePath);
  assert.doesNotMatch(source, /["']@agw\/desktop(?:[\/"'])/u, `${sourcePath} imports @agw/desktop`);
  assert.doesNotMatch(
    source,
    /["']@agw\/desktop-renderer(?:[\/"'])/u,
    `${sourcePath} imports the removed Desktop renderer package`,
  );
  assert.doesNotMatch(
    source,
    /["']@agw\/desktop-contracts(?:[\/"'])/u,
    `${sourcePath} imports Desktop bridge contracts`,
  );
  assert.doesNotMatch(
    source,
    /["']@tanstack\/react-query["']/u,
    `${sourcePath} bypasses @agw/components/query`,
  );
  assert.doesNotMatch(
    source,
    /["']@radix-ui\/react-accordion["']/u,
    `${sourcePath} bypasses @agw/components Accordion primitives`,
  );
  assert.doesNotMatch(source, /\bagwDesktop\b/u, `${sourcePath} accesses the Desktop preload`);
  assert.doesNotMatch(source, /desktop\/src/u, `${sourcePath} imports desktop/src`);
}

for (const filePath of [
  ...sourceFiles(join(clientsRoot, "desktop", "src")),
  ...sourceFiles(join(clientsRoot, "desktop", "renderer")),
  ...sourceFiles(join(clientsRoot, "desktop", "scripts")),
]) {
  const source = readFileSync(filePath, "utf8");
  const sourcePath = relative(clientsRoot, filePath);
  assert.doesNotMatch(source, /["']@agw\/web(?:[\/"'])/u, `${sourcePath} imports @agw/web`);
  assert.doesNotMatch(source, /web\/src/u, `${sourcePath} imports web/src`);
  assert.doesNotMatch(
    source,
    /["']@agw\/desktop-contracts(?:[\/"'])/u,
    `${sourcePath} imports the removed Desktop contracts package`,
  );
  assert.doesNotMatch(
    source,
    /["']@tanstack\/react-query["']/u,
    `${sourcePath} bypasses @agw/components/query`,
  );
  assert.doesNotMatch(
    source,
    /["']@radix-ui\/react-accordion["']/u,
    `${sourcePath} bypasses @agw/components Accordion primitives`,
  );
  assert.doesNotMatch(
    source,
    /\bwebDirectory\b|["']web["']/u,
    `${sourcePath} locates the Web application`,
  );
}

assert.equal(
  desktopManifest.scripts?.["prepare:renderer"],
  undefined,
  "@agw/desktop builds its own renderer and must not expose a cross-application prepare task",
);

const clientsManifest = readManifest(clientsRoot);
assert.equal(
  clientsManifest.scripts?.["prepare:renderer"],
  undefined,
  "The monorepo root must not assemble one application from another application's renderer",
);
assert.equal(
  existsSync(join(clientsRoot, "tools", "scripts", "prepare-desktop-renderer.mjs")),
  false,
  "The obsolete cross-application renderer assembly script must be removed",
);
for (const scriptName of ["package:desktop", "make:desktop"]) {
  assert.doesNotMatch(
    clientsManifest.scripts?.[scriptName] ?? "",
    /prepare:renderer|@agw\/web/u,
    `${scriptName} must package the independent Desktop application directly`,
  );
}

const packageDirectories = existsSync(packagesRoot)
  ? readdirSync(packagesRoot)
      .map((entry) => join(packagesRoot, entry))
      .filter((entry) => statSync(entry).isDirectory())
  : [];

for (const packageDirectory of packageDirectories) {
  const manifestPath = join(packageDirectory, "package.json");
  if (!existsSync(manifestPath)) continue;
  const manifest = readManifest(packageDirectory);
  assert.match(
    manifest.name,
    /^@agw\//u,
    `${relative(clientsRoot, manifestPath)} has invalid name`,
  );
  const selfImportPattern = new RegExp(
    `(?:from|import\\s*)\\s*\\(?["']${manifest.name.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&")}(?:[/"'])`,
    "u",
  );
  for (const filePath of sourceFiles(join(packageDirectory, "src"))) {
    assert.doesNotMatch(
      readFileSync(filePath, "utf8"),
      selfImportPattern,
      `${relative(clientsRoot, filePath)} imports its own package barrel`,
    );
  }
}

console.log(`Client package boundaries valid (${requiredPackages.length} required packages).`);
