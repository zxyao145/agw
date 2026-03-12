export default function Page() {
  function SummaryCards({ loading }: { loading: boolean }) {
    const cards = [
      { label: "Total Requests", value: 0, color: "text-light" },
      { label: "Error Rate", value: "0%", color: "text-rose" },
      { label: "Tasks Completed", value: 0, color: "text-sage" },
    ];

    return (
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {loading
          ? Array.from({ length: 4 }).map((_, i) => (
              <div
                key={i}
                className="animate-pulse rounded-xl border border-stone bg-charcoal p-4"
              >
                <div className="h-3 w-20 rounded bg-stone" />
                <div className="mt-2 h-6 w-12 rounded bg-stone" />
              </div>
            ))
          : cards.map((c) => (
              <div
                key={c.label}
                className="rounded-xl border border-stone bg-charcoal p-4"
              >
                <div className="text-xs text-dust">{c.label}</div>
                <div className={`mt-1 text-2xl font-bold ${c.color}`}>
                  {c.value}
                </div>
              </div>
            ))}
      </div>
    );
  }

  return (
    <div className="w-full mt-4">
      <SummaryCards loading={false} />
      <div className="h-screen bg-background w-full flex justify-center text-3xl text-blue-600">
        <p className="mt-40">Coming soon!</p>
      </div>
    </div>
  );
}
