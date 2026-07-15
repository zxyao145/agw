export interface SelectedOptionItem {
  id: string;
  title: string;
  description?: string;
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
