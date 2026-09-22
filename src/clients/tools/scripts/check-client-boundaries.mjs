import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative, resolve, sep } from "node:path";
import ts from "typescript";

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
  "execution-core",
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

const WORKSPACE_ROOTS = [
  mobileRoot,
  join(clientsRoot, "web"),
  join(clientsRoot, "desktop"),
  ...(existsSync(packagesRoot)
    ? readdirSync(packagesRoot).map((entry) => join(packagesRoot, entry))
    : []),
].filter((directory) => existsSync(join(directory, "package.json")));

function workspaceRootOf(filePath) {
  return WORKSPACE_ROOTS.find(
    (root) => filePath === root || filePath.startsWith(root + sep),
  );
}

function packageNameOf(filePath) {
  const packages = relative(packagesRoot, filePath);
  if (!packages.startsWith("..") && !packages.startsWith(sep)) {
    return packages.split(sep)[0];
  }
  if (filePath.startsWith(mobileRoot + sep)) return "mobile";
  if (filePath.startsWith(join(clientsRoot, "web") + sep)) return "web";
  if (filePath.startsWith(join(clientsRoot, "desktop") + sep)) return "desktop";
  return null;
}

// Extracts every import/require specifier with the TypeScript parser, so the boundary checks
// read the actual dependency edges of each file instead of scanning source text.
// 用 TypeScript 解析器提取每一条 import/require 说明符，让边界检查读到的是每个文件的
// 真实依赖边，而不是扫描源码文本。
function extractImportSpecifiers(filePath, sourceText) {
  const sourceFile = ts.createSourceFile(filePath, sourceText, ts.ScriptTarget.Latest, true);
  const specifiers = [];
  const visit = (node) => {
    if (ts.isImportDeclaration(node) && ts.isStringLiteral(node.moduleSpecifier)) {
      specifiers.push(node.moduleSpecifier.text);
    } else if (
      ts.isExportDeclaration(node) &&
      node.moduleSpecifier &&
      ts.isStringLiteral(node.moduleSpecifier)
    ) {
      specifiers.push(node.moduleSpecifier.text);
    } else if (
      ts.isImportEqualsDeclaration(node) &&
      ts.isExternalModuleReference(node.moduleReference) &&
      ts.isStringLiteral(node.moduleReference.expression)
    ) {
      specifiers.push(node.moduleReference.expression.text);
    } else if (
      ts.isCallExpression(node) &&
      node.expression.kind === ts.SyntaxKind.ImportKeyword &&
      node.arguments.length > 0 &&
      ts.isStringLiteral(node.arguments[0])
    ) {
      specifiers.push(node.arguments[0].text);
    }
    ts.forEachChild(node, visit);
  };
  visit(sourceFile);
  return specifiers;
}

const workspaceChecks = [
  {
    // A relative import must not leave the workspace root that owns the source file.
    // 相对导入不得离开源文件所属的工作区根目录。
    name: "relative import crosses a workspace boundary",
    appliesTo: () => true,
    test: (filePath, specifier) => {
      if (!specifier.startsWith(".")) return null;
      const targetRoot = workspaceRootOf(resolve(dirname(filePath), specifier));
      return targetRoot && targetRoot !== workspaceRootOf(filePath)
        ? `imports ${specifier} into another workspace`
        : null;
    },
  },
];

const packageImportChecks = [
  {
    name: "Web alias",
    appliesTo: (pkg) => pkg !== "mobile" && pkg !== "web" && pkg !== "desktop",
    test: (_pkg, specifier) =>
      /^@\//u.test(specifier) ? "imports the Web application alias" : null,
  },
  {
    name: "@agw/web",
    appliesTo: (pkg) => pkg !== "web" && pkg !== "desktop",
    test: (_pkg, specifier) =>
      /^@agw\/web(?:[\/"])/u.test(specifier) || specifier === "@agw/web"
        ? "imports the Web application"
        : null,
  },
  {
    name: "@agw/desktop-renderer",
    appliesTo: (pkg) => pkg !== "desktop",
    test: (_pkg, specifier) =>
      /^@agw\/desktop-renderer(?:[\/"])/u.test(specifier) ? "imports the removed Desktop renderer package" : null,
  },
  {
    name: "react-query bypass",
    appliesTo: (pkg) => pkg !== "components" && pkg !== "chat-native" && pkg !== "mobile",
    test: (_pkg, specifier) =>
      specifier === "@tanstack/react-query" ? "bypasses @agw/components/query" : null,
  },
  {
    name: "Accordion bypass",
    appliesTo: (pkg) => pkg !== "components" && pkg !== "chat-native" && pkg !== "mobile",
    test: (_pkg, specifier) =>
      specifier === "@radix-ui/react-accordion" ? "bypasses @agw/components Accordion primitives" : null,
  },
  {
    name: "platform renderer imports",
    appliesTo: (pkg) => pkg === "chat-core" || pkg === "chat-runtime",
    test: (_pkg, specifier) =>
      /^(?:next|react-dom|react-native|expo(?:-[^"']*)?|@agw\/components)(?:[\/"])/u.test(specifier) ||
      ["next", "react-dom", "react-native", "expo", "@agw/components"].includes(specifier)
        ? "imports a platform renderer dependency"
        : null,
  },
  {
    name: "Native renderer imports",
    appliesTo: (pkg) => pkg === "chat",
    test: (_pkg, specifier) =>
      /^(?:react-native|expo(?:-[^"']*)?)(?:[\/"])/u.test(specifier) ||
      ["react-native", "expo"].includes(specifier)
        ? "imports a Native renderer dependency"
        : null,
  },
  {
    name: "DOM renderer imports",
    appliesTo: (pkg) => pkg === "chat-native",
    test: (_pkg, specifier) =>
      /^(?:next|react-dom|@agw\/components|@agw\/chat)(?:[\/"])/u.test(specifier) ||
      ["next", "react-dom", "@agw/components", "@agw/chat"].includes(specifier)
        ? "imports a DOM renderer dependency"
        : null,
  },
  {
    name: "Desktop boundary",
    appliesTo: (pkg) => pkg === "web",
    test: (_pkg, specifier) =>
      /^@agw\/desktop(?:[\/"])/u.test(specifier) ||
      specifier === "@agw/desktop" ||
      /^@agw\/desktop-renderer(?:[\/"])/u.test(specifier) ||
      /^@agw\/desktop-contracts(?:[\/"])/u.test(specifier)
        ? "imports a Desktop boundary"
        : null,
  },
  {
    name: "Web boundary",
    appliesTo: (pkg) => pkg === "desktop",
    test: (_pkg, specifier) =>
      /^@agw\/web(?:[\/"])/u.test(specifier) ||
      specifier === "@agw/web" ||
      /^@agw\/desktop-contracts(?:[\/"])/u.test(specifier)
        ? "imports the removed Web boundary"
        : null,
  },
  {
    name: "Mobile Web/Desktop boundary",
    appliesTo: (pkg) => pkg === "mobile",
    test: (_pkg, specifier) =>
      /^@agw\/(?:web|desktop|components)(?:[\/"])/u.test(specifier) ||
      ["@agw/web", "@agw/desktop", "@agw/components"].includes(specifier)
        ? "imports a Web or Desktop boundary"
        : null,
  },
  {
    name: "Mobile Chat host",
    appliesTo: (pkg) => pkg === "mobile",
    test: (_pkg, specifier) =>
      /^@agw\/chat(?:[\/"])/u.test(specifier) || specifier === "@agw/chat"
        ? "must import @agw/chat-native"
        : null,
  },
  {
    name: "Mobile Projects core",
    appliesTo: (pkg) => pkg === "mobile",
    test: (_pkg, specifier) =>
      /^@agw\/projects(?:[\/"])/u.test(specifier) || specifier === "@agw/projects"
        ? "must import @agw/projects-core"
        : null,
  },
];

function checkSourceFile(filePath) {
  const source = readFileSync(filePath, "utf8");
  const sourcePath = relative(clientsRoot, filePath);
  const owner = packageNameOf(filePath);
  const specifiers = extractImportSpecifiers(filePath, source);

  for (const specifier of specifiers) {
    for (const check of workspaceChecks) {
      const message = check.test(filePath, specifier);
      if (message) {
        assert.fail(`${sourcePath} ${check.name}: ${message}`);
      }
    }
    if (!owner) continue;
    for (const check of packageImportChecks) {
      if (!check.appliesTo(owner)) continue;
      const message = check.test(owner, specifier);
      if (message) {
        assert.fail(`${sourcePath} ${check.name}: ${message}`);
      }
    }
  }

  // Runtime accessors that no import can express, verified against source content.
  // 导入无法表达的运行时访问，仍需对照源码内容核对。
  if (owner === "web") {
    assert.doesNotMatch(source, /\bagwDesktop\b/u, `${sourcePath} accesses the Desktop preload`);
  }
  if (owner === "desktop") {
    assert.doesNotMatch(source, /\bwebDirectory\b|["']web["']/u, `${sourcePath} locates the Web application`);
  }
}

const mobileSources = [...sourceFiles(join(mobileRoot, "app")), ...sourceFiles(join(mobileRoot, "src"))];
for (const filePath of mobileSources) {
  const source = readFileSync(filePath, "utf8");
  const sourcePath = relative(clientsRoot, filePath);
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
  checkSourceFile(filePath);
}

for (const filePath of sourceFiles(packagesRoot)) {
  checkSourceFile(filePath);
}

for (const filePath of [
  ...sourceFiles(join(packagesRoot, "chat-core", "src")),
  ...sourceFiles(join(packagesRoot, "chat-runtime", "src")),
  ...sourceFiles(join(packagesRoot, "chat", "src")),
  ...sourceFiles(join(packagesRoot, "chat-native", "src")),
]) {
  checkSourceFile(filePath);
}

for (const filePath of [
  join(mobileRoot, "src", "features", "chat", "message-rendering.ts"),
  join(mobileRoot, "src", "features", "chat", "image-picker.ts"),
]) {
  assert.equal(
    existsSync(filePath),
    false,
    `${relative(clientsRoot, filePath)} must live in @agw/chat-native`,
  );
}

for (const filePath of sourceFiles(join(clientsRoot, "web", "src"))) {
  checkSourceFile(filePath);
}

for (const filePath of [
  ...sourceFiles(join(clientsRoot, "desktop", "src")),
  ...sourceFiles(join(clientsRoot, "desktop", "renderer")),
  ...sourceFiles(join(clientsRoot, "desktop", "scripts")),
]) {
  checkSourceFile(filePath);
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
    `^(?:${manifest.name})(?:[/"])`,
    "u",
  );
  for (const filePath of sourceFiles(join(packageDirectory, "src"))) {
    const source = readFileSync(filePath, "utf8");
    for (const specifier of extractImportSpecifiers(filePath, source)) {
      assert.doesNotMatch(
        specifier,
        selfImportPattern,
        `${relative(clientsRoot, filePath)} imports its own package barrel`,
      );
    }
  }
}

console.log(`Client package boundaries valid (${requiredPackages.length} required packages).`);
