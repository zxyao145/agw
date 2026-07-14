import type { AppInstanceOption } from "./types";

export interface SelectedOptionItem {
  id: string;
  title: string;
  description?: string;
}

export function getAppAuthorizationState(
  app: Pick<AppInstanceOption, "isAuthorized" | "isAuthorizationExpired">,
): string {
  if (app.isAuthorizationExpired) {
    return "Expired";
  }

  if (app.isAuthorized) {
    return "Authorized";
  }

  return "Not authorized";
}

export function buildAppOptionLabel(
  app: Pick<AppInstanceOption, "displayName" | "clientId">,
): string {
  return `${app.displayName} · ${app.clientId}`;
}

export function buildSelectedSkillItems(
  selectedSkillIds: readonly string[],
  skills: readonly { id: string; name: string; description?: string }[],
): SelectedOptionItem[] {
  return selectedSkillIds.map((skillId) => {
    const skill = skills.find((candidate) => candidate.id === skillId);
    return skill
      ? { id: skillId, title: skill.name, description: skill.description }
      : { id: skillId, title: skillId, description: "Skill unavailable" };
  });
}

export function buildSelectedAppItems(
  selectedAppInstanceIds: readonly string[],
  appOptions: readonly AppInstanceOption[],
): SelectedOptionItem[] {
  return selectedAppInstanceIds.map((appInstanceId) => {
    const app = appOptions.find((candidate) => candidate.id === appInstanceId);
    return app
      ? {
          id: appInstanceId,
          title: buildAppOptionLabel(app),
          description: `${app.provider} · ${getAppAuthorizationState(app)}`,
        }
      : {
          id: appInstanceId,
          title: appInstanceId,
          description: "App connection unavailable",
        };
  });
}
