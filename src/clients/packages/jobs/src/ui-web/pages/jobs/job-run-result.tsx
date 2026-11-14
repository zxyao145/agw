import { Badge } from "@agw/components";

type JobRunResultVariant = "default" | "destructive" | "secondary";

export function getJobRunResultPresentation(result: number): {
  label: string;
  variant: JobRunResultVariant;
} {
  switch (result) {
    case 0:
      return { label: "Succeeded", variant: "default" };
    case 1:
      return { label: "Retry", variant: "secondary" };
    case 2:
      return { label: "Failed", variant: "destructive" };
    default:
      return { label: `Unknown (${result})`, variant: "secondary" };
  }
}

export function JobRunResultBadge({ result }: { result: number }) {
  const presentation = getJobRunResultPresentation(result);
  return <Badge variant={presentation.variant}>{presentation.label}</Badge>;
}
