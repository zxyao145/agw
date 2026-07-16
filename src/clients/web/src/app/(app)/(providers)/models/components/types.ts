import type { components } from "@/api/openapi";

export type ModelCreateRequest = components["schemas"]["ModelCreateRequest"];

export type ModelDto = {
  id: string;
  name: string;
  description: string | null;
  createBy?: string | null;
  createTime?: string | null;
  updateBy?: string | null;
  updateTime?: string | null;
};
