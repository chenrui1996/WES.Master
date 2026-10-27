import { apiBlob } from "../identity/identity-api";
import type { StagingFile } from "./staging-file";

/** Delivers only current, evidence-matching bytes; both file workspaces release browser resources. */
export async function downloadAuthorizedFile(path: string, file: StagingFile, signal: AbortSignal, current: () => boolean) {
  const content = await apiBlob(path, { signal });
  if (!current() || signal.aborted) return false;
  if (content.size !== file.byteCount || content.type.split(";")[0] !== file.contentType)
    throw new Error("下载内容证据与当前文件不符，未交付。");
  const url = URL.createObjectURL(content);
  const anchor = document.createElement("a");
  try {
    anchor.href = url; anchor.download = file.fileName; anchor.hidden = true;
    document.body.append(anchor); anchor.click();
    return true;
  } finally { anchor.remove(); URL.revokeObjectURL(url); }
}
