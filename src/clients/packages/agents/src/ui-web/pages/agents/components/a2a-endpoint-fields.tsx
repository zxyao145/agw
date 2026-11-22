import * as React from "react";
import { Button, Input, Label } from "@agw/components";
import { CardSummary, useA2ACard, type CardAction } from "./a2a-card";
import { A2ACredentialFields } from "./a2a-credential-fields";
import {
  A2A_ENDPOINT_LIMITS,
  getA2ACardUrlError,
  getA2ACredentialsError,
  type A2AEndpointDraft,
} from "./a2a-endpoint";
import type { A2AEndpointDto } from "./types";

export interface A2AEndpointFieldsProps {
  value: A2AEndpointDraft;
  onChange: (draft: A2AEndpointDraft) => void;
  interfaceUrl?: string;
  storedEndpoint?: A2AEndpointDto | null;
  agentId?: string;
  disabled?: boolean;
  dirty?: boolean;
  onPreview?: (name: string, description: string) => void;
  onRefresh?: (endpoint: A2AEndpointDto) => void;
}

export function A2AEndpointFields(props: A2AEndpointFieldsProps) {
  const { value, onChange, interfaceUrl, storedEndpoint } = props;
  const card = useA2ACard(props);
  const storedCard = storedEndpoint?.cardUrl === value.cardUrl ? storedEndpoint : undefined;
  const summary = card.preview ?? (storedCard && { ...storedCard, card: storedCard.extra.card });
  const fetchedAt = card.preview ? undefined : storedCard?.extra.cardFetchedAt;
  const urlError = getA2ACardUrlError(value.cardUrl);
  const credentialsError = getA2ACredentialsError(value.groups);
  const disabled = Boolean(props.disabled || card.pending);
  return (
    <section className="grid max-w-3xl gap-5">
      <CardUrlField
        value={value.cardUrl}
        disabled={disabled}
        error={urlError}
        onChange={(cardUrl) => {
          card.setPreview(null);
          card.setError(null);
          onChange({ ...value, cardUrl });
        }}
      />
      <A2ACredentialFields
        draft={value}
        interfaceUrl={summary?.interfaceUrl ?? interfaceUrl}
        disabled={disabled}
        error={credentialsError}
        onChange={(groups) => {
          card.setError(null);
          onChange({ ...value, groups });
        }}
      />
      <CardActions
        canFetch={!disabled && !urlError && !credentialsError}
        canRefresh={!disabled && !props.dirty}
        showRefresh={Boolean(props.agentId)}
        pending={card.pending}
        error={card.error}
        onLoad={card.loadCard}
      />
      {summary && <CardSummary card={summary} fetchedAt={fetchedAt} />}
    </section>
  );
}

interface CardUrlFieldProps {
  value: string;
  disabled: boolean;
  error: string | null;
  onChange: (value: string) => void;
}

function CardUrlField({ value, disabled, error, onChange }: CardUrlFieldProps) {
  const id = React.useId();
  return (
    <div className="grid gap-2">
      <Label htmlFor={id}>Agent Card URL</Label>
      <Input
        id={id}
        type="url"
        value={value}
        placeholder="https://example.com/.well-known/agent-card.json"
        required
        maxLength={A2A_ENDPOINT_LIMITS.cardUrlLength}
        disabled={disabled}
        aria-invalid={Boolean(value && error)}
        onChange={(event) => onChange(event.target.value)}
      />
      {value && error && <p className="text-xs text-destructive">{error}</p>}
    </div>
  );
}

interface CardActionsProps {
  canFetch: boolean;
  canRefresh: boolean;
  showRefresh: boolean;
  pending: CardAction | null;
  error: string | null;
  onLoad: (action: CardAction) => Promise<void>;
}

function CardActions(props: CardActionsProps) {
  const { canFetch, canRefresh, showRefresh, pending, error, onLoad } = props;
  return (
    <>
      <div className="flex flex-wrap gap-2">
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={!canFetch}
          onClick={() => void onLoad("fetch")}
        >
          {pending === "fetch" ? "Fetching card…" : "Fetch card"}
        </Button>
        {showRefresh && (
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={!canRefresh}
            onClick={() => void onLoad("refresh")}
          >
            {pending === "refresh" ? "Refreshing card…" : "Refresh card"}
          </Button>
        )}
      </div>
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
    </>
  );
}
