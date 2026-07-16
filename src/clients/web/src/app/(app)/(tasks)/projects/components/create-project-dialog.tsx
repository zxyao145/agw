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
  DialogTrigger,
} from "@/components/ui/dialog";

import {
  formatProjectFolderName,
  getProjectExtraSettingsError,
  normalizeProjectExtraSettings,
  resolveCreateProjectWorkspace,
  serializeProjectCapabilities,
} from "../project-form";
import { ProjectFormFields, type ProjectFormFieldsProps } from "./project-form-fields";
import type { ProjectCreateRequest } from "./types";

interface CreateProjectDialogProps extends Omit<
  ProjectFormFieldsProps,
  "extraSettingError" | "idPrefix"
> {
  open: boolean;
  setOpen: (open: boolean) => void;
  createProjectMutation: UseMutationResult<unknown, Error, ProjectCreateRequest, unknown>;
}

export function CreateProjectDialog({
  open,
  setOpen,
  createProjectMutation,
  name,
  description,
  workspace,
  extraSetting,
  environmentVariables,
  selectedTools,
  selectedSkillIds,
  selectedMcpToolServerIds,
  selectedConnectionIds,
  ...formProps
}: CreateProjectDialogProps) {
  const normalizedName = formatProjectFolderName(name);
  const extraSettingError = getProjectExtraSettingsError(extraSetting);
  const environmentVariablesError = getEnvironmentVariablesError(environmentVariables);

  const handleCreate = () => {
    if (!normalizedName || extraSettingError || environmentVariablesError) {
      return;
    }

    const capabilities = serializeProjectCapabilities({
      selectedTools,
      selectedSkillIds,
      selectedMcpToolServerIds,
      selectedConnectionIds,
      environmentVariables: normalizeEnvironmentVariables(environmentVariables),
    });

    createProjectMutation.mutate({
      name: normalizedName,
      description: description.length ? description : null,
      workspace: resolveCreateProjectWorkspace(normalizedName, workspace),
      extraSetting: normalizeProjectExtraSettings(extraSetting),
      ...capabilities,
    });
  };

  return (
    <Dialog
      open={open}
      onOpenChange={(nextOpen) =>
        applyDialogOpenChange({
          isPending: createProjectMutation.isPending,
          nextOpen,
          setOpen,
        })
      }
    >
      <DialogTrigger asChild>
        <Button>Create project</Button>
      </DialogTrigger>

      <DialogContent
        className="fixed inset-0 h-screen w-screen max-w-none translate-x-0 translate-y-0 gap-0 rounded-none border-0 p-0 sm:max-w-none"
        onInteractOutside={(event) => event.preventDefault()}
        onPointerDownOutside={(event) => event.preventDefault()}
        showCloseButton={false}
      >
        <div className="flex h-full min-h-0 flex-col">
          <DialogHeader className="shrink-0 border-b px-6 py-4">
            <div className="flex items-start justify-between gap-4">
              <div className="min-w-0">
                <DialogTitle>Create project</DialogTitle>
                <DialogDescription className="mt-1">
                  Define the project metadata and available capabilities.
                </DialogDescription>
              </div>
              <div className="flex shrink-0 items-center gap-2">
                <DialogClose asChild>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={createProjectMutation.isPending}
                  >
                    Cancel
                  </Button>
                </DialogClose>
                <Button
                  type="button"
                  size="sm"
                  onClick={handleCreate}
                  disabled={
                    !normalizedName ||
                    Boolean(extraSettingError) ||
                    Boolean(environmentVariablesError) ||
                    createProjectMutation.isPending
                  }
                >
                  {createProjectMutation.isPending ? "Creating..." : "Create"}
                </Button>
              </div>
            </div>
          </DialogHeader>

          <ProjectFormFields
            {...formProps}
            name={name}
            description={description}
            workspace={workspace}
            extraSetting={extraSetting}
            extraSettingError={extraSettingError}
            environmentVariables={environmentVariables}
            selectedTools={selectedTools}
            selectedSkillIds={selectedSkillIds}
            selectedMcpToolServerIds={selectedMcpToolServerIds}
            selectedConnectionIds={selectedConnectionIds}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
