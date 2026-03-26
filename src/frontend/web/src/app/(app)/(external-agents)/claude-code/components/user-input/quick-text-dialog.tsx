"use client";

import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Flashlight, Zap } from "lucide-react";
import { useState } from "react";

export interface QuickTextOption {
  id: string;
  label: string;
  text: string;
  description?: string;
}

interface QuickTextDialogProps {
  quickCommands: QuickTextOption[];
  onCommandSelect: (text: string) => void;
}

export function QuickTextDialog({ quickCommands, onCommandSelect }: QuickTextDialogProps) {
  const [open, setOpen] = useState(false);

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
            {quickCommands.map((option) => (
              <button
                key={option.id}
                onClick={() => handleSelect(option.text)}
                className="text-left p-2 rounded-md border hover:bg-accent/50 transition-colors"
              >
                <div className="flex items-start justify-between">
                  <div className="flex-1">
                    <div className="font-medium text-sm mb-1">{option.label}</div>
                    <div className="text-xs text-muted-foreground mb-0">{option.description}</div>
                    {/* <div className="text-xs bg-muted p-2 rounded font-mono">
                      {option.text}
                    </div> */}
                  </div>
                </div>
              </button>
            ))}
          </div>
        </ScrollArea>
      </DialogContent>
    </Dialog>
  );
}
