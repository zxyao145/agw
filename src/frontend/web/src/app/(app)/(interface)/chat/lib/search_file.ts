import { SuggestionItem } from "@/components/message/user-input";
import { searchFiles } from "@/api/files";

/**
 * Search for files by keyword
 * Returns async suggestions with file paths prefixed with @
 */
export async function searchFile(
  rootDirectory: string,
  keyword: string,
): Promise<SuggestionItem[]> {
  if (!rootDirectory) {
    return [];
  }
  // console.debug("searchFile", rootDirectory, keyword)

  try {
    const response = await searchFiles(rootDirectory, keyword, true);
    return response.results.map((result) => ({
      text: `@${result.relativePath}`,
      description: result.fullPath,
    }));
  } catch (error) {
    console.error("Failed to search files:", error);
    return [];
  }
}
