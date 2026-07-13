import * as React from "react";
import type { UseMutationResult } from "@tanstack/react-query";

import {
  applyDialogOpenChange,
  getEnvironmentVariablesError,
  normalizeEnvironmentVariables,
} from "@/components/definition-capabilities";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

import {
  getProjectExtraSettingsError,
  normalizeProjectExtraSettings,
  serializeProjectCapabilities,
} from "../project-form";
import { ProjectFormFields, type ProjectFormFieldsProps } from "./project-form-fields";
import type { ProjectResponse, ProjectUpdateMutationVariables } from "./types";

interface EditProjectDialogProps extends Omit<
  ProjectFormFieldsProps,
  "dialogPortalContainer" | "extraSettingError" | "idPrefix"
> {
  open: boolean;
  setOpen: (open: boolean) => void;
  editingProject: ProjectResponse | null;
  updateProjectMutation: UseMutationResult<unknown, Error, ProjectUpdateMutationVariables, unknown>;
}

export function EditProjectDialog({
  open,
  setOpen,
  editingProject,
  updateProjectMutation,
  name,
  description,
  workspace,
  enable,
  extraSetting,
  environmentVariables,
  selectedTools,
  selectedSkillIds,
  selectedMcpToolServerIds,
  selectedAppInstanceIds,
  ...formProps
}: EditProjectDialogProps) {
  const [dialogPortalContainer, setDialogPortalContainer] = React.useState<HTMLDivElement | null>(
    null,
  );
  const extraSettingError = getProjectExtraSettingsError(extraSetting);
  const environmentVariablesError = getEnvironmentVariablesError(environmentVariables);

  const handleUpdate = () => {
    if (
      !editingProject ||
      editingProject.type !== 0 ||
      !name.trim() ||
      extraSettingError ||
      environmentVariablesError
    ) {
      return;
    }

    const capabilities = serializeProjectCapabilities({
      selectedTools,
      selectedSkillIds,
      selectedMcpToolServerIds,
      selectedAppInstanceIds,
      environmentVariables: normalizeEnvironmentVariables(environmentVariables),
    });

    updateProjectMutation.mutate({
      project: editingProject,
      body: {
        name,
        description: description.length ? description : null,
        workspace: workspace.trim().length ? workspace.trim() : null,
        enable,
        extraSetting: normalizeProjectExtraSettings(extraSetting),
        ...capabilities,
      },
    });
  };

  return (
    <Dialog
      open={open && editingProject?.type === 0}
      onOpenChange={(nextOpen) => {
        if (nextOpen && editingProject?.type !== 0) {
          return;
        }

        applyDialogOpenChange({
          isPending: updateProjectMutation.isPending,
          nextOpen,
          setOpen,
        });
      }}
    >
      <DialogContent
        ref={setDialogPortalContainer}
        className="fixed inset-0 h-screen w-screen max-w-none translate-x-0 translate-y-0 gap-0 rounded-none border-0 p-0 sm:max-w-none"
        onInteractOutside={(event) => event.preventDefault()}
        onPointerDownOutside={(event) => event.preventDefault()}
        showCloseButton={false}
      >
        <div className="flex h-full min-h-0 flex-col">
          <DialogHeader className="shrink-0 border-b px-6 py-4">
            <div className="flex items-start justify-between gap-4">
              <div className="min-w-0">
                <DialogTitle>Edit project</DialogTitle>
                <DialogDescription className="mt-1">
                  Update the project metadata and available capabilities.
                </DialogDescription>
              </div>
              <div className="flex shrink-0 items-center gap-2">
                <DialogClose asChild>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={updateProjectMutation.isPending}
                  >
                    Cancel
                  </Button>
                </DialogClose>
                <Button
                  type="button"
                  size="sm"
                  onClick={handleUpdate}
                  disabled={
                    !editingProject ||
                    editingProject.type !== 0 ||
                    !name.trim() ||
                    Boolean(extraSettingError) ||
                    Boolean(environmentVariablesError) ||
                    updateProjectMutation.isPending
                  }
                >
                  {updateProjectMutation.isPending ? "Updating..." : "Update"}
                </Button>
              </div>
            </div>
          </DialogHeader>

          <ProjectFormFields
            {...formProps}
            dialogPortalContainer={dialogPortalContainer}
            idPrefix="edit-"
            name={name}
            description={description}
            workspace={workspace}
            enable={enable}
            extraSetting={extraSetting}
            extraSettingError={extraSettingError}
            environmentVariables={environmentVariables}
            selectedTools={selectedTools}
            selectedSkillIds={selectedSkillIds}
            selectedMcpToolServerIds={selectedMcpToolServerIds}
            selectedAppInstanceIds={selectedAppInstanceIds}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
