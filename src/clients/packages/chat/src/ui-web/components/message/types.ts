export type ProposedPlanPresentation = {
  leadingMarkdown: string;
  markdown: string;
  trailingMarkdown: string;
  isClosed: boolean;
};

export type MessageNode = {
  type: string;
  content: string;
  proposedPlan?: ProposedPlanPresentation;
};
