import { FILE_ENTRY_KIND, type FileEntryKindCode } from "$lib/api/generated/codes";

export type FileActionId =
  | "open"
  | "download"
  | "new-folder"
  | "upload"
  | "upload-media"
  | "upload-folder"
  | "rename"
  | "move"
  | "rescan"
  | "exclude"
  | "remove-exclusion"
  | "delete";

export interface FileAction {
  id: FileActionId;
  label: string;
  destructive?: boolean;
}

const rootActions: FileAction[] = [
  { id: "open", label: "Open" },
  { id: "download", label: "Download" },
  { id: "new-folder", label: "New folder" },
  { id: "upload", label: "Upload files" },
  { id: "upload-media", label: "Upload photos & videos" },
  { id: "upload-folder", label: "Upload folder" },
  { id: "rescan", label: "Rescan" },
];

const directoryActions: FileAction[] = [
  { id: "open", label: "Open" },
  { id: "download", label: "Download" },
  { id: "new-folder", label: "New folder" },
  { id: "upload", label: "Upload files" },
  { id: "upload-media", label: "Upload photos & videos" },
  { id: "upload-folder", label: "Upload folder" },
  { id: "rename", label: "Rename" },
  { id: "move", label: "Move" },
  { id: "rescan", label: "Rescan" },
  { id: "exclude", label: "Exclude" },
  { id: "delete", label: "Delete", destructive: true },
];

const fileActions: FileAction[] = [
  { id: "open", label: "Open" },
  { id: "download", label: "Download" },
  { id: "rename", label: "Rename" },
  { id: "move", label: "Move" },
  { id: "exclude", label: "Exclude" },
  { id: "delete", label: "Delete", destructive: true },
];

const removeExclusionAction: FileAction = { id: "remove-exclusion", label: "Remove exclusion" };

/**
 * Context actions for a Files entry. Read-only libraries only browse; `canPickFolders` hides the
 * folder upload where the browser cannot pick folders (iPhone and iPad).
 */
export function fileContextActions(
  kind: FileEntryKindCode,
  isRoot?: boolean,
  excluded?: boolean,
  isReadOnly?: boolean,
  canPickFolders = true,
): FileAction[] {
  if (isReadOnly) return rootActions.filter((action) => action.id === "open" || action.id === "download" || action.id === "rescan");
  const base = isRoot ? rootActions : kind === FILE_ENTRY_KIND.directory ? directoryActions : fileActions;
  const available = canPickFolders ? base : base.filter((action) => action.id !== "upload-folder");
  return excluded && !isRoot
    ? available.map((action) => action.id === "exclude" ? removeExclusionAction : action)
    : available;
}
