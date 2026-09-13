"use client";

import { createContext, useContext } from "react";

export type ProjectFileScope = {
  projectId?: string;
  directoryId?: string | null;
  directoryName?: string;
};

export const ProjectFileScopeContext = createContext<ProjectFileScope>({});
export const useProjectFileScope = () => useContext(ProjectFileScopeContext);
