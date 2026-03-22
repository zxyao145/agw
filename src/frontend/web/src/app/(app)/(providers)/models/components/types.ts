import type { components } from "@/api/openapi";

export type ModelCreateRequest = components["schemas"]["ModelCreateRequest"];

export type ModelDto = {
  id: string;
  name: string;
  description: string | null;
  type: number;
  maxTokens: number;
  createBy?: string | null;
  createTime?: string | null;
  updateBy?: string | null;
  updateTime?: string | null;
};

// [Flags] enum ModelType
export enum ModelType {
  Chat = 1,
  Image = 2,
  Audio = 4,
  Embedding = 8,
}

export const MODEL_TYPE_OPTIONS = [
  { value: ModelType.Chat, label: "Chat" },
  { value: ModelType.Image, label: "Image" },
  { value: ModelType.Audio, label: "Audio" },
  { value: ModelType.Embedding, label: "Embedding" },
];

export function getModelTypeLabel(type: number): string {
  const types: string[] = [];
  if (type & ModelType.Chat) types.push("Chat");
  if (type & ModelType.Image) types.push("Image");
  if (type & ModelType.Audio) types.push("Audio");
  if (type & ModelType.Embedding) types.push("Embedding");
  return types.length > 0 ? types.join(" | ") : "None";
}
