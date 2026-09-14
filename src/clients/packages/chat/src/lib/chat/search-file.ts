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
    const useRelativePaths = directoryIds.every((id) => id === null);
    return toFileSuggestions(response.results, useRelativePaths, 8).map((suggestion, index) => ({
      ...suggestion,
      description: response.results[index].relativePath,
    }));
  } catch (error) {
    console.error("Failed to search files:", error);
    return [];
  }
}
