import type { LocalSkillsSource } from "@agw/skills";

export const desktopLocalSkills: LocalSkillsSource = {
  scan: async () => {
    const bridge = window.agwDesktop;
    if (!bridge) throw new Error("Agw Desktop bridge is unavailable.");
    return bridge.scanLocalSkills();
  },
  pack: async (directoryName) => {
    const bridge = window.agwDesktop;
    if (!bridge) throw new Error("Agw Desktop bridge is unavailable.");
    return bridge.packLocalSkill(directoryName);
  },
};
