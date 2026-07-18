import { Table, TableBody, TableHeader } from "../shadcn/table";
import React, { ReactElement } from "react";
import { Empty } from "../shadcn/empty";

type SlotProps = {
  className?: string;
  children?: React.ReactNode;
};

export function StaticTable({
  isEmpty,
  children,
  embedded = false,
}: {
  isEmpty: boolean;
  children: React.ReactNode;
  embedded?: boolean;
}) {
  const childrenArray = React.Children.toArray(children) as ReactElement[];
  const empty = childrenArray.find((child) => child.type === Empty);
  const body = childrenArray.find((child) => child.type === TableBody);
  const header = childrenArray.find(
    (child) => child.type === TableHeader,
  ) as React.ReactElement<SlotProps>;

  if (isEmpty) {
    if (empty) {
      return empty;
    }
    return <div className="text-sm text-muted-foreground">{empty ? empty : "No data found."}</div>;
  }

  let renderedHeader = header;
  if (header && React.isValidElement(header)) {
    renderedHeader = React.cloneElement(header, {
      className: `${(header?.props as SlotProps)?.className ?? ""} bg-muted/30`,
    });
  }

  const restChildrenArray = childrenArray.filter(
    (child) => child != empty && child != header && child != body,
  );

  return (
    <div className={embedded ? "overflow-hidden" : "overflow-hidden rounded-md border"}>
      <Table className="min-w-240">
        {renderedHeader}
        {body}
        {restChildrenArray}
      </Table>
    </div>
  );
}
