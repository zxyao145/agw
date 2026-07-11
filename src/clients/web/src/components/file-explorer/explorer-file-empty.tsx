export default function ExplorerFileEmpty({
  rootDirectory,
}: {
  rootDirectory: string;
}): React.ReactNode {
  return (
    <div className="text-sm text-muted-foreground p-2 text-center">
      {rootDirectory
        ? "Directory is empty or cannot be accessed"
        : "Set a working directory in settings to browse files"}
    </div>
  );
}
