import type { UseMutationResult } from "@agw/components/query";

import {
  applyDialogOpenChange,
  getEnvironmentVariablesError,
  normalizeEnvironmentVariables,
} from "@agw/integrations";
import { Button } from "@agw/components";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@agw/components";

import {
  getProjectExtraSettingsError,
  normalizeProjectExtraSettings,
  serializeProjectCapabilities,
} from "../project-form";
import { ProjectFormFields, type ProjectFormFieldsProps } from "./project-form-fields";
import type { ProjectResponse, ProjectUpdateMutationVariables } from "./types";

interface EditProjectDialogProps extends Omit<
  ProjectFormFieldsProps,
  "extraSettingError" | "idPrefix"
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
  extraSetting,
  environmentVariables,
  tools,
  selectedSkillIds,
  selectedMcpToolServerIds,
  selectedConnectionIds,
  ...formProps
}: EditProjectDialogProps) {
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
      tools,
      selectedSkillIds,
      selectedMcpToolServerIds,
      selectedConnectionIds,
      environmentVariables: normalizeEnvironmentVariables(environmentVariables),
    });

    updateProjectMutation.mutate({
      project: editingProject,
      body: {
        name,
        description: description.length ? description : null,
        workspace: workspace.trim().length ? workspace.trim() : null,
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
        size="fullscreen"
        className="fixed inset-0 h-screen w-screen max-w-none translate-x-0 translate-y-0 gap-0 rounded-none border-0 p-0 sm:max-w-none"
        onInteractOutside={(event) => event.preventDefault()}
        onPointerDownOutside={(event) => event.preventDefault()}
        showCloseButton={false}
      >
        <div className="flex h-full min-h-0 flex-col">
          <DialogHeader className="shrink-0 border-b px-6 py-2">
            <div className="flex items-center justify-between gap-4">
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
            idPrefix="edit-"
            name={name}
            description={description}
            workspace={workspace}
            extraSetting={extraSetting}
            extraSettingError={extraSettingError}
            environmentVariables={environmentVariables}
            tools={tools}
            selectedSkillIds={selectedSkillIds}
            selectedMcpToolServerIds={selectedMcpToolServerIds}
            selectedConnectionIds={selectedConnectionIds}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
