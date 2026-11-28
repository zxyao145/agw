"use client";

import { SkillsPage } from "@agw/skills";

import { desktopLocalSkills } from "@/runtime";

export default function DesktopSkillsPage() {
  return <SkillsPage localSkills={desktopLocalSkills} />;
}
