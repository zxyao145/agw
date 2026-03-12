
export default function ExplorerFileError({
  message,
}: {
  message: string;
}): React.ReactNode {
  return (
    <div className="text-sm text-destructive p-2 bg-destructive/10 rounded">
      {message}
    </div>
  );
}
