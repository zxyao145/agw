import { EXTENSION_PERMISSIONS, type ExtensionManifest } from "@agw/extension-sdk";
import semver from "semver";
import { z } from "zod";
import {
  compareDesktopVersions,
  isSupportedDesktopVersion,
} from "../main/update/github-release-updater";
import type { ExtensionValidation, ExtensionValidationError } from "./contracts/extension";
import { HOST_PERMISSION_PATTERN } from "./extension-host-permissions";

export const EXTENSION_MANIFEST_VERSION_MIN = 1;
export const EXTENSION_MANIFEST_VERSION_CURRENT = 1;

// 包路径：根目录内的相对 POSIX 路径，不以 `/` 开头，不含 `\`、`.`/`..` 段与空段，不以 `__agw/` 开头。
// Package path: a relative POSIX path inside the root without a leading `/`, `\`, `.`/`..` or empty segments, and not under `__agw/`.
const PACKAGE_PATH = String.raw`(?!/)(?!__agw(?:/|$))(?!.*\\)(?!(?:.*/)?\.{1,2}(?:/|$))(?!.*//)[^\u0000]*[^/\u0000]`;
const ICON_FILE = String.raw`${PACKAGE_PATH}\.(?:svg|png)`;
const LUCIDE_ICON = String.raw`[A-Z][A-Za-z0-9]*`;

const PACKAGE_PATH_PATTERN = new RegExp(`^${PACKAGE_PATH}$`, "u");
const PACKAGE_ICON_PATTERN = new RegExp(`^${ICON_FILE}$`, "u");
const ENTRY_PATTERN = new RegExp(String.raw`^${PACKAGE_PATH}\.html$`, "u");
const BACKGROUND_PATTERN = new RegExp(String.raw`^${PACKAGE_PATH}\.m?js$`, "u");
const THEME_PATTERN = new RegExp(String.raw`^${PACKAGE_PATH}\.(?:json|css)$`, "u");
const ICON_PATTERN = new RegExp(`^(?:${LUCIDE_ICON}|${ICON_FILE})$`, "u");
// `data` 与 `packages` 是 `<userData>/extensions` 下的目录名，不能作为 id。
// `data` and `packages` name the directories under `<userData>/extensions` and cannot be ids.
const EXTENSION_ID_PATTERN = /^(?!agw)(?!(?:data|packages)$)[a-z0-9]+(?:-[a-z0-9]+)*$/u;
const LOCAL_ID_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/u;

export function isValidPackagePath(value: string): boolean {
  return PACKAGE_PATH_PATTERN.test(value);
}

/** 图标是包内文件（否则是 lucide-react 导出名）。The icon is a package file (otherwise a lucide-react export name). */
export function isPackageIconPath(icon: string): boolean {
  return PACKAGE_ICON_PATTERN.test(icon);
}

const localIdSchema = z.string().max(64).regex(LOCAL_ID_PATTERN);
const titleSchema = z.string().min(1).max(64);
const iconSchema = z.string().regex(ICON_PATTERN);
const entrySchema = z.string().regex(ENTRY_PATTERN);

const actionSchema = z.discriminatedUnion("type", [
  z.strictObject({ type: z.literal("openMainView"), view: localIdSchema }),
  z.strictObject({ type: z.literal("toggleSidebarView"), view: localIdSchema }),
  z.strictObject({ type: z.literal("command"), command: localIdSchema }),
]);

const actionContributionSchema = z.strictObject({
  id: localIdSchema,
  title: titleSchema,
  icon: iconSchema,
  action: actionSchema,
});

// 文件查看器的文件名模式：不含路径分隔符。File viewer name patterns contain no path separators.
const fileViewerPatternSchema = z
  .string()
  .min(1)
  .max(64)
  .regex(new RegExp(String.raw`^[^/\\\u0000]+$`, "u"));

export const extensionManifestSchema = z
  .strictObject({
    $schema: z.string().optional(),
    manifestVersion: z.number().int(),
    id: z.string().min(3).max(64).regex(EXTENSION_ID_PATTERN),
    name: z.string().min(1).max(64),
    version: z.string().refine((value) => semver.valid(value) !== null, {
      message: "Version must be a valid semantic version.",
    }),
    minAppVersion: z
      .string()
      .refine(isSupportedDesktopVersion, { message: "minAppVersion must be a Desktop version." })
      .optional(),
    description: z.string().max(500).optional(),
    author: z.string().optional(),
    authorUrl: z.url({ protocol: /^https?$/u }).optional(),
    icon: z.string().regex(PACKAGE_ICON_PATTERN).optional(),
    background: z.string().regex(BACKGROUND_PATTERN).optional(),
    contributes: z
      .strictObject({
        commands: z.array(z.strictObject({ id: localIdSchema, title: titleSchema })).optional(),
        ribbon: z.array(actionContributionSchema).optional(),
        sidebarViews: z
          .array(
            z.strictObject({
              id: localIdSchema,
              title: titleSchema,
              icon: iconSchema,
              entry: entrySchema,
            }),
          )
          .optional(),
        mainViews: z
          .array(
            z.strictObject({
              id: localIdSchema,
              title: titleSchema,
              icon: iconSchema,
              entry: entrySchema,
              retainContextWhenHidden: z.boolean().optional(),
            }),
          )
          .optional(),
        settings: z.strictObject({ entry: entrySchema }).optional(),
        chat: z
          .strictObject({
            composerActions: z.array(actionContributionSchema).optional(),
            messageActions: z.array(actionContributionSchema).optional(),
            conversationActions: z.array(actionContributionSchema).optional(),
          })
          .optional(),
        explorerActions: z.array(actionContributionSchema).optional(),
        fileViewers: z
          .array(
            z.strictObject({
              id: localIdSchema,
              title: titleSchema,
              icon: iconSchema,
              patterns: z.array(fileViewerPatternSchema).min(1).max(20),
              entry: entrySchema,
              priority: z.enum(["default", "option"]).optional(),
            }),
          )
          .optional(),
        themes: z
          .array(
            z.strictObject({
              id: localIdSchema,
              title: titleSchema,
              path: z.string().regex(THEME_PATTERN),
            }),
          )
          .optional(),
      })
      .optional(),
    permissions: z.array(z.enum(EXTENSION_PERMISSIONS)).optional(),
    hostPermissions: z.array(z.string().regex(HOST_PERMISSION_PATTERN)).optional(),
  })
  .superRefine((manifest, context) => {
    const report = (code: string, message: string, path: (string | number)[]) =>
      context.addIssue({ code: "custom", message, path, params: { code } });

    const contributes = manifest.contributes ?? {};
    const unique = (items: readonly { id: string }[] | undefined, path: (string | number)[]) =>
      items?.forEach((item, index) => {
        if (items.findIndex((other) => other.id === item.id) !== index) {
          report("DUPLICATE_ID", `Duplicate id "${item.id}".`, [...path, index, "id"]);
        }
      });
    unique(contributes.commands, ["contributes", "commands"]);
    unique(contributes.ribbon, ["contributes", "ribbon"]);
    unique(contributes.sidebarViews, ["contributes", "sidebarViews"]);
    unique(contributes.mainViews, ["contributes", "mainViews"]);
    unique(contributes.chat?.composerActions, ["contributes", "chat", "composerActions"]);
    unique(contributes.chat?.messageActions, ["contributes", "chat", "messageActions"]);
    unique(contributes.chat?.conversationActions, ["contributes", "chat", "conversationActions"]);
    unique(contributes.explorerActions, ["contributes", "explorerActions"]);
    unique(contributes.fileViewers, ["contributes", "fileViewers"]);
    unique(contributes.themes, ["contributes", "themes"]);

    // 侧栏视图、主区视图与文件查看器共用 `<extensionId>:<id>` 视图 id。
    // Sidebar views, main views and file viewers share the `<extensionId>:<id>` view id.
    const viewOwners = new Map<string, string>();
    for (const collection of ["sidebarViews", "mainViews", "fileViewers"] as const) {
      contributes[collection]?.forEach((view, index) => {
        const owner = viewOwners.get(view.id);
        if (owner !== undefined && owner !== collection) {
          report("DUPLICATE_ID", `View id "${view.id}" is also used in ${owner}.`, [
            "contributes",
            collection,
            index,
            "id",
          ]);
        }
        viewOwners.set(view.id, owner ?? collection);
      });
    }

    const uniqueValues = (values: readonly string[] | undefined, path: (string | number)[]) =>
      values?.forEach((value, index) => {
        if (values.indexOf(value) !== index) {
          report("DUPLICATE_VALUE", `Duplicate value "${value}".`, [...path, index]);
        }
      });
    uniqueValues(manifest.permissions, ["permissions"]);
    uniqueValues(manifest.hostPermissions, ["hostPermissions"]);
    contributes.fileViewers?.forEach((viewer, index) =>
      uniqueValues(viewer.patterns, ["contributes", "fileViewers", index, "patterns"]),
    );

    const mainViews = new Set(contributes.mainViews?.map((view) => view.id));
    const sidebarViews = new Set(contributes.sidebarViews?.map((view) => view.id));
    const commands = new Set(contributes.commands?.map((command) => command.id));
    const checkActions = (
      items: readonly { action: z.infer<typeof actionSchema> }[] | undefined,
      path: (string | number)[],
    ) =>
      items?.forEach(({ action }, index) => {
        const actionPath = [...path, index, "action"];
        if (action.type === "openMainView" && !mainViews.has(action.view)) {
          report("UNKNOWN_VIEW", `Main view "${action.view}" is not declared.`, [
            ...actionPath,
            "view",
          ]);
        } else if (action.type === "toggleSidebarView" && !sidebarViews.has(action.view)) {
          report("UNKNOWN_VIEW", `Sidebar view "${action.view}" is not declared.`, [
            ...actionPath,
            "view",
          ]);
        } else if (action.type === "command") {
          if (!commands.has(action.command)) {
            report("UNKNOWN_COMMAND", `Command "${action.command}" is not declared.`, [
              ...actionPath,
              "command",
            ]);
          }
          if (manifest.background === undefined) {
            report(
              "BACKGROUND_REQUIRED",
              "Command actions require a background script.",
              actionPath,
            );
          }
        }
      });
    checkActions(contributes.ribbon, ["contributes", "ribbon"]);
    checkActions(contributes.chat?.composerActions, ["contributes", "chat", "composerActions"]);
    checkActions(contributes.chat?.messageActions, ["contributes", "chat", "messageActions"]);
    checkActions(contributes.chat?.conversationActions, [
      "contributes",
      "chat",
      "conversationActions",
    ]);
    checkActions(contributes.explorerActions, ["contributes", "explorerActions"]);
  });

// SDK 的 `sdk.d.ts` 使用 `@agw/extension-sdk` 中显式声明的类型；这里保证它与 schema 推导出的类型相同。
// The SDK's `sdk.d.ts` uses the explicit type in `@agw/extension-sdk`; this keeps it identical to the type inferred from the schema.
type MutuallyAssignable<A, B> = [A] extends [B] ? ([B] extends [A] ? true : false) : false;
const manifestTypeMatchesSchema: MutuallyAssignable<
  z.output<typeof extensionManifestSchema>,
  ExtensionManifest
> = true;
void manifestTypeMatchesSchema;

export type ManifestEvaluation = {
  manifest: ExtensionManifest | null;
  validation: ExtensionValidation;
  errors: ExtensionValidationError[];
};

function formatPath(path: readonly PropertyKey[]): string | null {
  if (path.length === 0) return null;
  return path.reduce<string>((result, segment) => {
    if (typeof segment === "number") return `${result}[${segment}]`;
    return result.length === 0 ? String(segment) : `${result}.${String(segment)}`;
  }, "");
}

/** zod 问题转换为校验错误，`prefix` 加在每个字段路径之前。Converts zod issues; `prefix` goes before every field path. */
export function toValidationErrors(
  issues: readonly z.core.$ZodIssue[],
  prefix: readonly PropertyKey[] = [],
): ExtensionValidationError[] {
  return issues.flatMap((issue): ExtensionValidationError[] => {
    if (issue.code === "unrecognized_keys") {
      return issue.keys.map((key) => ({
        code: "UNKNOWN_FIELD",
        message: `Unknown field "${key}".`,
        path: formatPath([...prefix, ...issue.path, key]),
      }));
    }
    const code =
      issue.code === "custom" && typeof issue.params?.code === "string"
        ? issue.params.code
        : "INVALID_FIELD";
    return [{ code, message: issue.message, path: formatPath([...prefix, ...issue.path]) }];
  });
}

function manifestVersionErrors(raw: unknown): ExtensionValidationError[] {
  if (typeof raw !== "object" || raw === null) return [];
  const version = (raw as { manifestVersion?: unknown }).manifestVersion;
  if (typeof version !== "number" || !Number.isInteger(version)) return [];
  if (version < EXTENSION_MANIFEST_VERSION_MIN) {
    return [
      {
        code: "MANIFEST_VERSION_UNSUPPORTED",
        message: `This extension targets Extension API version ${version}. This Desktop supports version ${EXTENSION_MANIFEST_VERSION_MIN} and later.`,
        path: "manifestVersion",
      },
    ];
  }
  if (version > EXTENSION_MANIFEST_VERSION_CURRENT) {
    return [
      {
        code: "MANIFEST_VERSION_TOO_NEW",
        message: `This extension targets Extension API version ${version}. Update Agw Desktop to version ${EXTENSION_MANIFEST_VERSION_CURRENT} of the Extension API or later.`,
        path: "manifestVersion",
      },
    ];
  }
  return [];
}

/** 校验 manifest 并检查版本。Validates a manifest and checks versions. */
export function evaluateExtensionManifest(raw: unknown, appVersion: string): ManifestEvaluation {
  const parsed = extensionManifestSchema.safeParse(raw);
  const versionErrors = manifestVersionErrors(raw);
  if (versionErrors.length > 0) {
    return {
      manifest: parsed.success ? parsed.data : null,
      validation: "incompatible",
      errors: versionErrors,
    };
  }
  if (!parsed.success) {
    return {
      manifest: null,
      validation: "invalid",
      errors: toValidationErrors(parsed.error.issues),
    };
  }

  const manifest = parsed.data;
  if (
    manifest.minAppVersion !== undefined &&
    compareDesktopVersions(appVersion, manifest.minAppVersion) < 0
  ) {
    return {
      manifest,
      validation: "incompatible",
      errors: [
        {
          code: "APP_VERSION_TOO_OLD",
          message: `This extension requires Agw Desktop ${manifest.minAppVersion} or later. The current version is ${appVersion}.`,
          path: "minAppVersion",
        },
      ],
    };
  }
  return { manifest, validation: "valid", errors: [] };
}

export type ManifestPackagePath = {
  path: string;
  /** manifest 中的字段路径。Field path in the manifest. */
  field: string;
  kind: "entry" | "background" | "icon" | "theme";
};

/** manifest 引用的全部包路径。Every package path the manifest references. */
export function manifestPackagePaths(manifest: ExtensionManifest): ManifestPackagePath[] {
  const paths: ManifestPackagePath[] = [];
  const icon = (value: string, field: string) => {
    if (isPackageIconPath(value)) paths.push({ path: value, field, kind: "icon" });
  };
  if (manifest.icon !== undefined) icon(manifest.icon, "icon");
  if (manifest.background !== undefined) {
    paths.push({ path: manifest.background, field: "background", kind: "background" });
  }
  const contributes = manifest.contributes ?? {};
  contributes.ribbon?.forEach((item, index) =>
    icon(item.icon, `contributes.ribbon[${index}].icon`),
  );
  contributes.sidebarViews?.forEach((view, index) => {
    icon(view.icon, `contributes.sidebarViews[${index}].icon`);
    paths.push({
      path: view.entry,
      field: `contributes.sidebarViews[${index}].entry`,
      kind: "entry",
    });
  });
  contributes.mainViews?.forEach((view, index) => {
    icon(view.icon, `contributes.mainViews[${index}].icon`);
    paths.push({ path: view.entry, field: `contributes.mainViews[${index}].entry`, kind: "entry" });
  });
  contributes.fileViewers?.forEach((viewer, index) => {
    icon(viewer.icon, `contributes.fileViewers[${index}].icon`);
    paths.push({
      path: viewer.entry,
      field: `contributes.fileViewers[${index}].entry`,
      kind: "entry",
    });
  });
  if (contributes.settings) {
    paths.push({
      path: contributes.settings.entry,
      field: "contributes.settings.entry",
      kind: "entry",
    });
  }
  contributes.themes?.forEach((theme, index) =>
    paths.push({ path: theme.path, field: `contributes.themes[${index}].path`, kind: "theme" }),
  );
  const actions = (items: readonly { icon: string }[] | undefined, field: string) =>
    items?.forEach((item, index) => icon(item.icon, `${field}[${index}].icon`));
  actions(contributes.chat?.composerActions, "contributes.chat.composerActions");
  actions(contributes.chat?.messageActions, "contributes.chat.messageActions");
  actions(contributes.chat?.conversationActions, "contributes.chat.conversationActions");
  actions(contributes.explorerActions, "contributes.explorerActions");
  return paths;
}

/** manifest 的 JSON Schema，供编辑器提示与检查。The manifest JSON Schema for editor hints and checks. */
export function extensionManifestJsonSchema(): Record<string, unknown> {
  return z.toJSONSchema(extensionManifestSchema) as Record<string, unknown>;
}
