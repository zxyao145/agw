"use client";

import * as React from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { AlertCircle, ArrowLeft, CheckCircle2, ShieldAlert } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { formatLocalDateTime } from "@/lib/date-time";

function findMatchingAuthRequest(state: string | null) {
  if (!state || typeof window === "undefined") {
    return null;
  }

  for (let index = 0; index < sessionStorage.length; index += 1) {
    const key = sessionStorage.key(index);
    if (!key?.startsWith("agw.oauth2.")) {
      continue;
    }

    const rawValue = sessionStorage.getItem(key);
    if (!rawValue) {
      continue;
    }

    try {
      const parsed = JSON.parse(rawValue) as {
        state?: string;
        integrationId?: string;
        createdAt?: string;
      };
      if (parsed.state === state) {
        return { key, ...parsed };
      }
    } catch {
      // Ignore malformed session storage values.
    }
  }

  return null;
}

export default function IntegrationsCallbackPage() {
  const searchParams = useSearchParams();
  const code = searchParams.get("code");
  const state = searchParams.get("state");
  const error = searchParams.get("error");
  const exchangeStatus = searchParams.get("exchange_status");
  const exchangeError = searchParams.get("exchange_error");
  const provider = searchParams.get("provider");
  const subject = searchParams.get("subject");
  const matchingRequest = React.useMemo(() => findMatchingAuthRequest(state), [state]);
  const isStateVerified = Boolean(state && matchingRequest);

  return (
    <div className="flex w-full max-w-3xl items-start justify-center py-6">
      <Card className="w-full">
        <CardHeader>
          <CardTitle>Integration callback</CardTitle>
          <CardDescription>
            Review the OAuth2 provider response and the backend token exchange result.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="flex flex-wrap gap-2">
            <Badge variant={error ? "destructive" : "default"}>
              {error ? "Authorization failed" : "Authorization received"}
            </Badge>
            <Badge variant={isStateVerified ? "secondary" : "outline"}>
              {isStateVerified ? "State verified" : "State not verified"}
            </Badge>
            {exchangeStatus ? (
              <Badge variant={exchangeStatus === "success" ? "secondary" : "destructive"}>
                {exchangeStatus === "success" ? "Token stored" : "Token exchange failed"}
              </Badge>
            ) : null}
          </div>

          <div className="rounded-lg border p-4">
            <div className="flex items-start gap-3">
              {error ? (
                <AlertCircle className="mt-0.5 size-5 text-destructive" />
              ) : isStateVerified ? (
                <CheckCircle2 className="mt-0.5 size-5 text-primary" />
              ) : (
                <ShieldAlert className="mt-0.5 size-5 text-amber-500" />
              )}
              <div className="space-y-2 text-sm">
                {error ? (
                  <p className="text-muted-foreground">
                    The external provider returned an OAuth2 error of{" "}
                    <span className="font-medium">{error}</span>.
                  </p>
                ) : exchangeStatus === "success" ? (
                  <p className="text-muted-foreground">
                    The backend exchanged the authorization code and persisted the token for{" "}
                    <span className="font-medium">
                      {provider ?? matchingRequest?.integrationId ?? "the integration"}
                    </span>
                    .
                  </p>
                ) : exchangeStatus === "failed" ? (
                  <p className="text-muted-foreground">
                    The provider returned an authorization code, but the backend token exchange
                    failed with{" "}
                    <span className="font-medium">{exchangeError ?? "an unknown error"}</span>.
                  </p>
                ) : code ? (
                  <p className="text-muted-foreground">
                    An authorization code was returned and forwarded through Agw's backend callback.
                  </p>
                ) : (
                  <p className="text-muted-foreground">
                    No authorization code or error was present in the callback URL.
                  </p>
                )}
              </div>
            </div>
          </div>

          <dl className="grid gap-4 rounded-lg border p-4 text-sm sm:grid-cols-2">
            <div>
              <dt className="font-medium">Authorization code</dt>
              <dd className="mt-1 break-all text-muted-foreground">{code ?? "-"}</dd>
            </div>
            <div>
              <dt className="font-medium">State</dt>
              <dd className="mt-1 break-all text-muted-foreground">{state ?? "-"}</dd>
            </div>
            <div>
              <dt className="font-medium">Integration</dt>
              <dd className="mt-1 text-muted-foreground">
                {provider ?? matchingRequest?.integrationId ?? "Unknown"}
              </dd>
            </div>
            <div>
              <dt className="font-medium">Started at</dt>
              <dd className="mt-1 text-muted-foreground">
                {matchingRequest?.createdAt
                  ? formatLocalDateTime(matchingRequest.createdAt)
                  : "Unknown"}
              </dd>
            </div>
            <div>
              <dt className="font-medium">Exchange status</dt>
              <dd className="mt-1 text-muted-foreground">{exchangeStatus ?? "Not attempted"}</dd>
            </div>
            <div>
              <dt className="font-medium">Stored subject</dt>
              <dd className="mt-1 break-all text-muted-foreground">{subject ?? "-"}</dd>
            </div>
          </dl>

          <Button asChild variant="outline">
            <Link href="/integrations">
              <ArrowLeft className="mr-2 size-4" />
              Back to integrations
            </Link>
          </Button>
        </CardContent>
      </Card>
    </div>
  );
}
