export type { ProposedPlanPresentation } from "@agw/chat-core";

import type { ProposedPlanPresentation } from "@agw/chat-core";

export type MessageNode = {
  type: string;
  content: string;
  name?: string;
  proposedPlan?: ProposedPlanPresentation;
};
