import { searchFilesInDirectories } from "@agw/projects";
import { toFileSuggestions, type SuggestionItem } from "@agw/chat-core";

export async function searchFile(
  projectId: string | null,
  keyword: string,
  directoryIds: readonly (string | null)[] = [null],
): Promise<SuggestionItem[]> {
  if (!projectId) {
    return [];
  }

  try {
    const response = await searchFilesInDirectories(projectId, "", keyword, true, directoryIds);
    return toFileSuggestions(
      response.results,
      directoryIds.every((id) => id === null),
    );
  } catch (error) {
    console.error("Failed to search files:", error);
    return [];
  }
}
