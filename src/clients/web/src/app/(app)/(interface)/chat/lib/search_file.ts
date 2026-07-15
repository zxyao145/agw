import type { SuggestionItem } from "@/components/message/user-input";
import { searchFiles } from "@/api/files";

/**
 * Search for files by keyword
 * Returns async suggestions with file paths prefixed with @
 */
export async function searchFile(
  projectId: string | null,
  keyword: string,
): Promise<SuggestionItem[]> {
  if (!projectId) {
    return [];
  }
  // console.debug("searchFile", rootDirectory, keyword)

  try {
    const response = await searchFiles(projectId, "", keyword, true);
    return response.results.map((result) => ({
      text: `@${result.relativePath}`,
      description: result.fullPath,
    }));
  } catch (error) {
    console.error("Failed to search files:", error);
    return [];
  }
}
