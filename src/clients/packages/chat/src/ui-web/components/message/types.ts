export type ProposedPlanPresentation = {
  markdown: string;
  trailingMarkdown: string;
  isClosed: boolean;
};

export type MessageNode = {
  type: string;
  content: string;
  proposedPlan?: ProposedPlanPresentation;
};
