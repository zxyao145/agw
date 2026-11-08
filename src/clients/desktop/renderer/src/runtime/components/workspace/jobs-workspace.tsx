"use client";

import * as React from "react";
import { JobLogsPage, JobsPage } from "@agw/jobs";

export function JobsWorkspace({ onOpenChat }: { onOpenChat: () => void }) {
  const [jobId, setJobId] = React.useState<string | null>(null);

  return (
    <div className="min-h-0 flex-1 overflow-y-auto p-6 agw-scrollbar">
      {jobId ? (
        <JobLogsPage
          jobId={jobId}
          chatBasePath="/desktop/chat"
          onBack={() => setJobId(null)}
          onOpenChat={onOpenChat}
        />
      ) : (
        <JobsPage onViewLogs={setJobId} />
      )}
    </div>
  );
}
