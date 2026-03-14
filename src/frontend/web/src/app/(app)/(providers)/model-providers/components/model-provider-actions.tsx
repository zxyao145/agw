"use client"

import * as React from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { BookKey, KeyRound, Trash2 } from "lucide-react"
import { toast } from "sonner"

import { apiDelete, apiPost, apiPut } from "@/api/client"
import { Button } from "@/components/ui/button"
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription as UiDialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle as UiDialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { ButtonGroup } from "@/components/ui/button-group"
import type { ApiKeyCreateRequest, ApiKeyUpdateRequest } from "./types"
import { getApiErrorMessage, listKeysByPair } from "./utils"
import { Switch } from "./switch"

type ModelProviderActionsProps = {
  modelProviderId: string
  modelId: string
  providerId: string
}

export function ModelProviderActions({
  modelProviderId,
  modelId,
  providerId,
}: ModelProviderActionsProps) {
  const queryClient = useQueryClient()

  const deleteModelProviderMutation = useMutation({
    mutationFn: async () => {
      const keys = await listKeysByPair({ modelProviderId })
      await Promise.all(
        keys.map((k) =>
          // @ts-expect-error - OpenAPI schema has incorrect top-level path parameters definition
          apiDelete("/api/model-provider-keys/{id}", { params: { path: { id: k.id } } })
        )
      )
      // @ts-expect-error - OpenAPI schema has incorrect top-level path parameters definition
      await apiDelete("/api/model-providers/{modelProviderId}", {
        params: { path: { modelProviderId } },
      })
    },
    onSuccess: async () => {
      toast.success("Deleted model provider and keys")
      await queryClient.invalidateQueries({ queryKey: ["model-providers"] })
      await queryClient.invalidateQueries({ queryKey: ["model-provider-keys"] })
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`)
    },
  })


  return (
    <div className="flex justify-end gap-2">
      <ButtonGroup>
        <Button
          type="button"
          className="cursor-pointer"
          variant="destructive"
          size="sm"
          disabled={deleteModelProviderMutation.isPending}
          onClick={() => {
            const ok = window.confirm(
              "Delete this model-provider link and all related keys?"
            );
            if (!ok) return;
            deleteModelProviderMutation.mutate();
          }}
        >
          <Trash2 className="w-4 h-4" />
        </Button>
      </ButtonGroup>
    </div>
  );
}
