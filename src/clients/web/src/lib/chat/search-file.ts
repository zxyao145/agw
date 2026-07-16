import { searchFiles } from "@/api/files";
import type { SuggestionItem } from "@/components/message/user-input";

export async function searchFile(
  projectId: string | null,
  keyword: string,
): Promise<SuggestionItem[]> {
  if (!projectId) {
    return [];
  }

  try {
    const response = await searchFiles(projectId, "", keyword, true);
    return response.results.slice(0, 5).map((result) => ({
      text: `@${result.relativePath}`,
      description: result.fullPath,
    }));
  } catch (error) {
    console.error("Failed to search files:", error);
    return [];
  }
}
