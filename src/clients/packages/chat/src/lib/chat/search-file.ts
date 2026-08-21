import { searchFiles } from "@agw/projects";
import { toFileSuggestions, type SuggestionItem } from "@agw/chat-core";

export async function searchFile(
  projectId: string | null,
  keyword: string,
): Promise<SuggestionItem[]> {
  if (!projectId) {
    return [];
  }

  try {
    const response = await searchFiles(projectId, "", keyword, true);
    return toFileSuggestions(response.results);
  } catch (error) {
    console.error("Failed to search files:", error);
    return [];
  }
}
