import { Trash2 } from "lucide-react";
import React from "react";
import { Textarea } from "@/components/ui/textarea";
import { CommentSide, CommentSideLabel, LineComment } from "./types";

interface CommentInputProps {
  value: string;
  onChange: (value: string) => void;
  onSubmit: () => void;
  onCancel: () => void;
  placeholder: string;
  autoFocus?: boolean;
}

function CommentInput({
  value,
  onChange,
  onSubmit,
  onCancel,
  placeholder,
  autoFocus = false,
}: CommentInputProps) {
  return (
    <div className="p-0">
      <Textarea
        value={value}
        onChange={(e) => onChange(e.target.value)}
        onBlur={() => {
          onSubmit();
        }}
        placeholder={placeholder}
        className="min-h-20 text-sm resize-none bg-background"
        autoFocus={autoFocus}
        onKeyDown={(e) => {
          if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
            if (value.trim()) {
              onSubmit();
            }
          }
          if (e.key === "Escape") {
            onCancel();
          }
        }}
      />
      <div className="flex items-center justify-between mt-2.5">
        <span className="text-xs text-muted-foreground">Ctrl+Enter to submit, Esc to cancel</span>
      </div>
    </div>
  );
}

export interface CommentSectionProps {
  lineComments: LineComment[];
  isCommentActive: boolean;
  onAddComment: (content: string) => void;
  onDeleteComment: (commentId: string) => void;
  onUpdateComment: (commentId: string, newContent: string) => void;
  isDiffView?: boolean;
  commentSide?: CommentSide;
}

export function CommentSection({
  lineComments,
  isCommentActive,
  onAddComment,
  onDeleteComment,
  onUpdateComment,
  isDiffView = false,
  commentSide = CommentSide.Current,
}: CommentSectionProps) {
  const [editingCommentId, setEditingCommentId] = React.useState<string | null>(null);
  const [editContent, setEditContent] = React.useState("");

  const handleStartEditing = (commentId: string, content: string) => {
    setEditingCommentId(commentId);
    setEditContent(content);
  };

  const handleSaveEdit = () => {
    if (editingCommentId) {
      // Existing comment
      if (editContent.trim()) {
        onUpdateComment(editingCommentId, editContent.trim());
      } else {
        onDeleteComment(editingCommentId);
      }
    } else {
      // New comment
      if (editContent.trim()) {
        onAddComment(editContent.trim());
      }
    }
    setEditingCommentId(null);
    setEditContent("");
  };

  const handleCancelEdit = () => {
    setEditingCommentId(null);
    setEditContent("");
  };

  const placeholder = editingCommentId
    ? ""
    : isDiffView
      ? `Write a comment for ${CommentSideLabel[commentSide]}...`
      : "Write a comment...";

  return (
    <div className="pl-3">
      {/* Existing comments */}
      {lineComments.map((comment) => (
        <div key={comment.id} className="p-3 border-b border-border last:border-b-0">
          <div className="flex items-start justify-between gap-2">
            <div className="flex-1 min-w-0">
              <div className="flex items-center gap-2 mb-1.5 flex-wrap">
                <span className="text-xs text-muted-foreground font-medium">
                  {comment.timestamp.toLocaleTimeString()}
                </span>
              </div>
              {editingCommentId === comment.id ? (
                <CommentInput
                  value={editContent}
                  onChange={setEditContent}
                  onSubmit={handleSaveEdit}
                  onCancel={handleCancelEdit}
                  placeholder={placeholder}
                  autoFocus
                />
              ) : (
                <p
                  className="text-sm whitespace-pre-wrap wrap-break-word cursor-text hover:bg-muted/50 rounded px-1 -mx-1 transition-colors"
                  onDoubleClick={() => handleStartEditing(comment.id, comment.content)}
                  title="Double-click to edit"
                >
                  {comment.content}
                </p>
              )}
            </div>
            <button
              onClick={() => onDeleteComment(comment.id)}
              className="text-destructive hover:bg-destructive/10 p-1.5 rounded transition-colors cursor-pointer"
              title="Delete comment"
            >
              <Trash2 className="h-3.5 w-3.5" />
            </button>
          </div>
        </div>
      ))}

      {/* Add comment input */}
      {isCommentActive && (
        <div className="p-3">
          <CommentInput
            value={editContent}
            onChange={setEditContent}
            onSubmit={handleSaveEdit}
            onCancel={handleCancelEdit}
            placeholder={placeholder}
            autoFocus
          />
        </div>
      )}
    </div>
  );
}
