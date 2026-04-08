import { cn } from "@/lib/utils";
import { GripVertical } from "lucide-react";
import React, { FC, ReactElement } from "react";

export interface ColSplitProps {
  children: React.ReactNode;
}

interface SlotProps {
  children: React.ReactNode;
}

interface LeftSlotProps extends SlotProps {
  minWidth?: number;
  maxWidth?: number;
}

// 定义组件类型，包含命名插槽
interface ColResizeSplitComponent extends FC<ColSplitProps> {
  Left: FC<LeftSlotProps>;
  Right: FC<SlotProps>;
}

const ColResizeSplit: ColResizeSplitComponent = ({ children }: ColSplitProps) => {
  // 拆分 children，找到 left 和 right
  let left: React.ReactNode = null;
  let right: React.ReactNode = null;
  let leftProps: LeftSlotProps = { children: null };
  let rightProps: SlotProps = { children: null };

  React.Children.forEach(children, (child) => {
    if (!React.isValidElement(child)) return;

    // 这里做类型断言
    const element = child as ReactElement<SlotProps>;

    if (element.type === ColResizeSplit.Left) {
      leftProps = element.props as LeftSlotProps;
      left = leftProps.children;
    } else if (element.type === ColResizeSplit.Right) {
      rightProps = element.props;
      right = rightProps.children;
    }
  });

  const [isResizing, setIsResizing] = React.useState(false);
  const resizeRef = React.useRef<HTMLDivElement>(null);
  const [panelWidth, setPanelWidth] = React.useState(320);

  // Handle resize
  React.useEffect(() => {
    const handleMouseMove = (e: MouseEvent) => {
      if (!isResizing) return;

      const containerRect = resizeRef.current?.parentElement?.getBoundingClientRect();
      if (!containerRect) return;

      const newWidth = e.clientX - containerRect.left;
      const minWidth = leftProps?.minWidth ?? 200;
      const maxWidth = leftProps?.maxWidth ?? 600;

      // Clamp between min 200px and max 600px
      setPanelWidth(Math.max(minWidth, Math.min(maxWidth, newWidth)));
    };

    const handleMouseUp = () => {
      setIsResizing(false);
      document.body.style.cursor = "";
      document.body.style.userSelect = "";
    };

    if (isResizing) {
      document.body.style.cursor = "col-resize";
      document.body.style.userSelect = "none";
      document.addEventListener("mousemove", handleMouseMove);
      document.addEventListener("mouseup", handleMouseUp);
    }

    return () => {
      document.removeEventListener("mousemove", handleMouseMove);
      document.removeEventListener("mouseup", handleMouseUp);
    };
  }, [isResizing]);

  return (
    <div className="flex flex-col flex-1 min-h-0 h-full">
      <div className="w-full flex flex-1 flex-row min-h-0 h-full">
        {left && (
          <>
            <div
              className="flex flex-col min-h-0 overflow-hidden h-full"
              ref={resizeRef}
              style={{ width: panelWidth }}
            >
              {left}
            </div>
            {/* Resize handle */}
            <div
              className={cn(
                "w-1 cursor-col-resize flex items-center justify-center bg-primary/20 transition-colors group",
                isResizing && "bg-primary/30",
              )}
              onMouseDown={(e) => {
                e.preventDefault();
                setIsResizing(true);
              }}
            >
              <GripVertical className="h-4 w-4 text-muted-foreground opacity-0 group-hover:opacity-100 transition-opacity" />
            </div>
          </>
        )}

        <div className="flex flex-1 min-h-0 overflow-hidden h-full">{right}</div>
      </div>
    </div>
  );
};

// 定义命名插槽组件
ColResizeSplit.Left = ({ children }) => children;
ColResizeSplit.Right = ({ children }) => children;

export default ColResizeSplit;
