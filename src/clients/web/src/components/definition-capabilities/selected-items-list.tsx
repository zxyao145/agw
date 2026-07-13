import { X } from "lucide-react";

import { Button } from "@/components/ui/button";
import {
  Item,
  ItemActions,
  ItemContent,
  ItemDescription,
  ItemGroup,
  ItemTitle,
} from "@/components/ui/item";

export type SelectedItem = {
  id: string;
  title: string;
  description?: string;
};

interface SelectedItemsListProps {
  items: SelectedItem[];
  emptyLabel: string;
  onRemove: (id: string) => void;
  readOnly?: boolean;
}

export function SelectedItemsList({
  items,
  emptyLabel,
  onRemove,
  readOnly = false,
}: SelectedItemsListProps) {
  if (items.length === 0) {
    return (
      <div className="rounded-lg border border-dashed bg-muted/20 px-4 py-8 text-center text-sm text-muted-foreground">
        {emptyLabel}
      </div>
    );
  }

  return (
    <ItemGroup className="gap-2">
      {items.map((item) => (
        <Item key={item.id} variant="outline" size="sm" className="bg-background/70">
          <ItemContent className="min-w-0">
            <ItemTitle className="max-w-full truncate">{item.title}</ItemTitle>
            {item.description ? (
              <ItemDescription className="line-clamp-2 text-xs">{item.description}</ItemDescription>
            ) : null}
          </ItemContent>
          {!readOnly ? (
            <ItemActions>
              <Button
                type="button"
                variant="ghost"
                size="icon-sm"
                aria-label={`Remove ${item.title}`}
                onClick={() => onRemove(item.id)}
              >
                <X />
              </Button>
            </ItemActions>
          ) : null}
        </Item>
      ))}
    </ItemGroup>
  );
}
