"use client";

import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@agw/components";
import { Button } from "@agw/components";
import { Badge } from "@agw/components";
import { ScrollArea } from "@agw/components";
import { Zap } from "lucide-react";
import { useEffect, useState } from "react";
import { listQuickPrompts, type QuickPrompt } from "@agw/api";

export interface QuickTextOption {
  id: string;
  label: string;
  text: string;
  description?: string | null;
  kind?: "system" | "user";
}

interface QuickTextDialogProps {
  quickCommands?: QuickTextOption[];
  onCommandSelect: (text: string) => void;
}

export function QuickTextDialog({ quickCommands, onCommandSelect }: QuickTextDialogProps) {
  const [open, setOpen] = useState(false);
  const [loadedCommands, setLoadedCommands] = useState<QuickTextOption[]>(quickCommands ?? []);
  const [loading, setLoading] = useState(false);
  useEffect(() => {
    if (quickCommands) return;
    setLoading(true);
    void listQuickPrompts()
      .then((items: QuickPrompt[]) => setLoadedCommands(items))
      .catch(() => setLoadedCommands([]))
      .finally(() => setLoading(false));
  }, [quickCommands, open]);

  const handleSelect = (text: string) => {
    onCommandSelect(text);
    setOpen(false);
  };

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button variant="ghost" size="sm" className="flex justify-center items-center">
          <Zap className="w-4 h-4" />
        </Button>
      </DialogTrigger>
      <DialogContent size="md">
        <DialogHeader>
          <DialogTitle>Quick Text Insert</DialogTitle>
          <DialogDescription>
            {/* Select a predefined text template to insert into the input field */}
          </DialogDescription>
        </DialogHeader>
        <ScrollArea className="max-h-100 pr-4">
          <div className="grid gap-2">
            {loading ? (
              <div className="p-4 text-sm text-muted-foreground">Loading…</div>
            ) : (
              loadedCommands.map((option) => (
                <button
                  key={`${option.kind ?? "user"}:${option.id}`}
                  onClick={() => handleSelect(option.text)}
                  className="cursor-pointer text-left p-2 rounded-md border hover:bg-accent/50 transition-colors"
                >
                  <div className="flex items-start justify-between">
                    <div className="flex-1">
                      <div className="mb-1 flex items-center gap-2 font-medium text-sm">
                        <span>{option.label}</span>
                        {option.kind ? (
                          <Badge variant="outline" className="px-1.5 py-0 text-[10px] uppercase">
                            {option.kind}
                          </Badge>
                        ) : null}
                      </div>
                      <div className="text-xs text-muted-foreground mb-0">{option.description}</div>
                      {/* <div className="text-xs bg-muted p-2 rounded font-mono">
                      {option.text}
                    </div> */}
                    </div>
                  </div>
                </button>
              ))
            )}
          </div>
        </ScrollArea>
      </DialogContent>
    </Dialog>
  );
}
