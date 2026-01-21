import type { components } from "@/api/openapi"

export type ProviderCreateRequest =
  components["schemas"]["ProviderCreateRequest"]

export type ProviderDto = {
  id: string
  name: string
  description: string | null
  endpoint: string
  createBy?: string | null
  createTime?: string | null
  updateBy?: string | null
  updateTime?: string | null
}
