"use client";

import * as React from "react";
import {
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Textarea,
} from "@agw/components";
import { Copy, Download } from "lucide-react";
import { toast } from "sonner";

import type { AgentflowEditorDocument } from "./agentflow-document";
import { useAgentflowEditorStoreApi } from "./agentflow-editor-store";
import {
  createAgentflowTransfer,
  parseAgentflowTransfer,
  type AgentflowTransfer,
} from "./agentflow-transfer";

type LayoutImportedDocument = (
  document: AgentflowEditorDocument,
) => Promise<AgentflowEditorDocument>;

export function useAgentflowTransferDialogs(arrange: LayoutImportedDocument) {
  const store = useAgentflowEditorStoreApi();
  const [dialog, setDialog] = React.useState<"import" | "export" | null>(null);
  const [text, setText] = React.useState("");
  const { errors, submit, importing, clearErrors } = useImportDefinition(arrange);
  const close = () => setDialog(null);
  const beginImport = () => {
    setText("");
    clearErrors();
    setDialog("import");
  };
  const beginExport = () => {
    setText(JSON.stringify(createAgentflowTransfer(store.getState().document), null, 2));
    setDialog("export");
  };
  return {
    beginImport,
    beginExport,
    isOpen: dialog !== null,
    dialogs:
      dialog === "import" ? (
        <ImportDialog
          text={text}
          errors={errors}
          importing={importing}
          onClose={close}
          onTextChange={setText}
          onImport={async () => {
            if (await submit(text)) close();
          }}
          onReadFile={async (file) => {
            setText(await file.text());
            clearErrors();
          }}
        />
      ) : dialog === "export" ? (
        <ExportDialog text={text} name={store.getState().document.name} onClose={close} />
      ) : null,
  };
}

function useImportDefinition(arrange: LayoutImportedDocument) {
  const store = useAgentflowEditorStoreApi();
  const [errors, setErrors] = React.useState<string[]>([]);
  const [importing, setImporting] = React.useState(false);
  const clearErrors = () => setErrors([]);
  const submit = async (text: string) => {
    const parsed = parseAgentflowTransfer(text);
    if (!parsed.ok) {
      setErrors(parsed.errors.map((error) => `${error.field}: ${error.value || "Invalid value"}`));
      return false;
    }
    setImporting(true);
    try {
      const { updateDocument, document } = store.getState();
      const imported = await prepareImportedDocument(document, parsed.transfer, arrange);
      updateDocument(() => imported);
      clearErrors();
      return true;
    } finally {
      setImporting(false);
    }
  };
  return { errors, submit, importing, clearErrors };
}

async function prepareImportedDocument(
  document: AgentflowEditorDocument,
  transfer: AgentflowTransfer,
  arrange: LayoutImportedDocument,
) {
  const imported = {
    ...document,
    ...transfer.definition,
    name: transfer.name,
    description: transfer.description ?? "",
  };
  const missingPosition = imported.nodes.some((node) => !node.position);
  return missingPosition ? await arrange(imported) : imported;
}

type ImportDialogProps = {
  text: string;
  errors: string[];
  importing: boolean;
  onTextChange(text: string): void;
  onReadFile(file: File): Promise<void>;
  onClose(): void;
  onImport(): Promise<void>;
};

function ImportDialog({
  text,
  errors,
  importing,
  onTextChange,
  onReadFile,
  onClose,
  onImport,
}: ImportDialogProps) {
  return (
    <TransferDialog
      title="Import Agentflow"
      description="Choose an Agentflow JSON file or paste its contents."
      onClose={onClose}
    >
      <Input
        type="file"
        accept=".json,.agentflow.json,application/json"
        aria-label="Choose Agentflow JSON file"
        onChange={(event) => {
          const file = event.target.files?.[0];
          if (file) void onReadFile(file);
        }}
      />
      <Textarea
        aria-label="Agentflow JSON"
        value={text}
        onChange={(event) => onTextChange(event.target.value)}
        rows={8}
        className="max-h-[45vh] overflow-y-auto"
      />
      <ImportErrors errors={errors} />
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button type="button" onClick={() => void onImport()} disabled={importing}>
          {importing ? "Importing…" : "Import definition"}
        </Button>
      </DialogFooter>
    </TransferDialog>
  );
}

function ImportErrors({ errors }: { errors: string[] }) {
  if (errors.length === 0) return null;
  return (
    <ul className="space-y-1 text-sm text-destructive">
      {errors.map((error, index) => (
        <li key={`${error}-${index}`}>{error}</li>
      ))}
    </ul>
  );
}

function ExportDialog({ text, name, onClose }: { text: string; name: string; onClose(): void }) {
  const copy = async () => {
    await navigator.clipboard.writeText(text);
    toast.success("Copied Agentflow JSON");
  };
  return (
    <TransferDialog
      title="Export Agentflow"
      description="Copy or download the Agentflow definition."
      onClose={onClose}
    >
      <Textarea aria-label="Agentflow JSON" readOnly value={text} rows={14} />
      <DialogFooter>
        <Button type="button" variant="outline" onClick={() => void copy()}>
          <Copy className="mr-2 h-4 w-4" /> Copy JSON
        </Button>
        <Button type="button" onClick={() => downloadAgentflow(text, name)}>
          <Download className="mr-2 h-4 w-4" /> Download JSON
        </Button>
      </DialogFooter>
    </TransferDialog>
  );
}

function TransferDialog({
  title,
  description,
  onClose,
  children,
}: React.PropsWithChildren<{
  title: string;
  description: string;
  onClose(): void;
}>) {
  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>
        {children}
      </DialogContent>
    </Dialog>
  );
}

function downloadAgentflow(text: string, name: string) {
  const url = URL.createObjectURL(new Blob([text], { type: "application/json" }));
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = `${name || "agentflow"}.agentflow.json`;
  anchor.click();
  URL.revokeObjectURL(url);
}
