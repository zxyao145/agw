import { Button, Input } from "@agw/components";
import {
  A2A_ENDPOINT_LIMITS,
  getA2ACredentialGroups,
  isA2AOriginWarningVisible,
  type A2ACredentialGroup,
  type A2ACredentialHeaderRow,
  type A2AEndpointDraft,
} from "./a2a-endpoint";

interface A2ACredentialFieldsProps {
  draft: A2AEndpointDraft;
  interfaceUrl?: string;
  disabled: boolean;
  error: string | null;
  onChange: (groups: A2ACredentialGroup[]) => void;
}

export function A2ACredentialFields({
  draft,
  interfaceUrl,
  disabled,
  error,
  onChange,
}: A2ACredentialFieldsProps) {
  const groups = getA2ACredentialGroups(draft.cardUrl, interfaceUrl, draft.groups);
  if (groups.length === 0) return null;
  const originLimitReached = draft.groups.length >= A2A_ENDPOINT_LIMITS.origins;
  return (
    <div className="grid gap-3">
      <h3 className="text-sm font-medium">Credentials</h3>
      {groups.map((group) => (
        <CredentialGroupFields
          key={group.origin}
          group={group}
          disabled={disabled}
          originLimitReached={originLimitReached}
          onChange={(rows) => {
            const next = draft.groups.filter(({ origin }) => origin !== group.origin);
            if (rows.length > 0) next.push({ origin: group.origin, rows });
            onChange(next);
          }}
        />
      ))}
      {error && <p className="text-xs text-destructive">{error}</p>}
    </div>
  );
}

interface CredentialGroupFieldsProps {
  group: A2ACredentialGroup;
  disabled: boolean;
  originLimitReached: boolean;
  onChange: (rows: A2ACredentialHeaderRow[]) => void;
}

function CredentialGroupFields(props: CredentialGroupFieldsProps) {
  const {
    group: { origin, rows },
    disabled,
    originLimitReached,
    onChange,
  } = props;
  const canAddHeader =
    !disabled &&
    rows.length < A2A_ENDPOINT_LIMITS.headersPerOrigin &&
    (rows.length > 0 || !originLimitReached);
  return (
    <fieldset className="grid gap-2 rounded-lg border px-3 pb-3">
      <legend className="max-w-full break-all px-1 font-mono text-xs">{origin}</legend>
      {rows.map((row, index) => (
        <CredentialHeaderFields
          key={index}
          row={row}
          number={index + 1}
          disabled={disabled}
          onChange={(updated) => onChange(rows.map((item, i) => (i === index ? updated : item)))}
          onRemove={() => onChange(rows.filter((_, i) => i !== index))}
        />
      ))}
      {isA2AOriginWarningVisible(origin, rows) && (
        <p className="text-xs text-muted-foreground">
          Headers for this origin are sent unencrypted over HTTP.
        </p>
      )}
      <Button
        type="button"
        variant="outline"
        size="sm"
        className="justify-self-start"
        disabled={!canAddHeader}
        onClick={() => onChange([...rows, { origin, name: "", value: "", saved: false }])}
      >
        Add header
      </Button>
    </fieldset>
  );
}

interface CredentialHeaderFieldsProps {
  row: A2ACredentialHeaderRow;
  number: number;
  disabled: boolean;
  onChange: (row: A2ACredentialHeaderRow) => void;
  onRemove: () => void;
}

function CredentialHeaderFields(props: CredentialHeaderFieldsProps) {
  const { row, number, disabled, onChange, onRemove } = props;
  return (
    <div className="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] items-center gap-2">
      <Input
        aria-label={`Header name ${number}`}
        placeholder="Authorization"
        value={row.name}
        disabled={disabled}
        onChange={(event) => {
          const name = event.target.value;
          const saved = row.saved && row.name.toLowerCase() === name.toLowerCase();
          onChange({ ...row, name, saved });
        }}
      />
      <Input
        aria-label={`Header value ${number}`}
        autoComplete="new-password"
        type="password"
        placeholder={row.saved ? "Keep saved value" : "Header value"}
        value={row.value}
        maxLength={A2A_ENDPOINT_LIMITS.headerValueLength}
        disabled={disabled}
        onChange={(event) => onChange({ ...row, value: event.target.value })}
      />
      <Button
        type="button"
        variant="ghost"
        size="sm"
        aria-label={`Remove header ${number}`}
        disabled={disabled}
        onClick={onRemove}
      >
        Remove
      </Button>
    </div>
  );
}
