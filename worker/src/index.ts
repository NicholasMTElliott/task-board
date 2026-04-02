import { neon } from "@neondatabase/serverless";

export interface Env {
  // Trello webhook config
  TRELLO_API_SECRET: string;
  TRELLO_WEBHOOK_CALLBACK_URL: string;
  // GitHub webhook config
  GITHUB_WEBHOOK_SECRET?: string;
  // Shared
  NEON_DATABASE_URL: string;
  PGMQ_QUEUE_NAME?: string;        // legacy full-payload queue (default: "events")
  PGMQ_PING_QUEUE_NAME?: string;   // lightweight ping queue (default: "pings")
  LAMBDA_KICK_URL?: string;
  INTERNAL_KICK_SECRET?: string;
}

export type SqlFunction = (
  strings: TemplateStringsArray,
  ...values: unknown[]
) => Promise<Record<string, unknown>[]>;

export interface WebhookDependencies {
  enqueuePing: (sql: SqlFunction, queueName: string, source: string) => Promise<number>;
  enqueueWebhookEvent: (
    sql: SqlFunction,
    queueName: string,
    actionId: string,
    cardId: string | null,
    payload: TrelloWebhookPayload
  ) => Promise<number>;
  kickLambda: (env: Env, actionId: string) => Promise<void>;
}

export interface TrelloWebhookPayload {
  action?: {
    id?: string;
    type?: string;
    data?: {
      card?: {
        id?: string;
      };
      listBefore?: {
        id?: string;
      };
      listAfter?: {
        id?: string;
      };
    };
  };
}

const jsonResponse = (status: number, body: unknown): Response =>
  new Response(JSON.stringify(body), {
    status,
    headers: {
      "content-type": "application/json; charset=utf-8"
    }
  });

// ── Shared helpers ───────────────────────────────────────────────────────────

function normalizeMessageId(messageId: unknown): number {
  const normalized =
    typeof messageId === "number"
      ? messageId
      : typeof messageId === "string"
        ? Number.parseInt(messageId, 10)
        : Number.NaN;

  if (!Number.isFinite(normalized)) {
    throw new Error("PGMQ enqueue did not return a message_id");
  }
  return normalized;
}

function validateQueueName(queueName: string): void {
  if (!/^[a-zA-Z0-9_-]+$/.test(queueName)) {
    throw new Error(`Invalid queue name: ${queueName}`);
  }
}

// ── Ping enqueueing (shared across all providers) ────────────────────────────

export const enqueuePing = async (
  sql: SqlFunction,
  queueName: string,
  source: string
): Promise<number> => {
  validateQueueName(queueName);
  const ping = JSON.stringify({
    source,
    ts: new Date().toISOString()
  });

  const rows = await sql`
    select pgmq.send(${queueName}, ${ping}::jsonb) as message_id
  `;

  return normalizeMessageId((rows as Array<{ message_id: unknown }>)[0]?.message_id);
};

// ── Legacy full-payload enqueueing (Trello backward compat) ──────────────────

export const enqueueWebhookEvent = async (
  sql: SqlFunction,
  queueName: string,
  actionId: string,
  cardId: string | null,
  payload: TrelloWebhookPayload
): Promise<number> => {
  validateQueueName(queueName);
  const queuePayload = {
    actionId,
    cardId,
    receivedAtUtc: new Date().toISOString(),
    payload
  };

  const queuePayloadJson = JSON.stringify(queuePayload);

  const rows = await sql`
    select pgmq.send(${queueName}, ${queuePayloadJson}::jsonb) as message_id
  `;

  return normalizeMessageId((rows as Array<{ message_id: unknown }>)[0]?.message_id);
};

// ── Trello webhook verification (HMAC-SHA1) ──────────────────────────────────

export const verifyTrelloWebhook = async (
  body: string,
  signatureHeader: string | null,
  callbackUrl: string,
  apiSecret: string
): Promise<boolean> => {
  if (!signatureHeader) return false;

  const encoder = new TextEncoder();
  const key = await crypto.subtle.importKey(
    "raw",
    encoder.encode(apiSecret),
    { name: "HMAC", hash: "SHA-1" },
    false,
    ["sign"]
  );

  const signed = await crypto.subtle.sign(
    "HMAC",
    key,
    encoder.encode(body + callbackUrl)
  );

  const expectedSignature = btoa(
    String.fromCharCode(...new Uint8Array(signed))
  );

  return constantTimeEqual(encoder, expectedSignature, signatureHeader);
};

// ── GitHub webhook verification (HMAC-SHA256) ────────────────────────────────

export const verifyGitHubWebhook = async (
  body: string,
  signatureHeader: string | null,
  secret: string
): Promise<boolean> => {
  if (!signatureHeader || !secret) return false;

  // GitHub sends "sha256=<hex>"
  const prefix = "sha256=";
  if (!signatureHeader.startsWith(prefix)) return false;
  const receivedHex = signatureHeader.slice(prefix.length);

  const encoder = new TextEncoder();
  const key = await crypto.subtle.importKey(
    "raw",
    encoder.encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"]
  );

  const signed = await crypto.subtle.sign("HMAC", key, encoder.encode(body));
  const expectedHex = Array.from(new Uint8Array(signed))
    .map(b => b.toString(16).padStart(2, "0"))
    .join("");

  return constantTimeEqual(encoder, expectedHex, receivedHex);
};

function constantTimeEqual(encoder: TextEncoder, a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  const ab = encoder.encode(a);
  const bb = encoder.encode(b);
  let mismatch = 0;
  for (let i = 0; i < ab.length; i++) {
    mismatch |= ab[i] ^ bb[i];
  }
  return mismatch === 0;
}

// ── Trello helpers ───────────────────────────────────────────────────────────

export const extractActionId = (payload: TrelloWebhookPayload): string | null =>
  payload.action?.id ?? null;

export const extractCardId = (payload: TrelloWebhookPayload): string | null =>
  payload.action?.data?.card?.id ?? null;

// ── Lambda kick ──────────────────────────────────────────────────────────────

export const getLambdaKickUrl = (env: Env): string | null => {
  const configuredUrl = env.LAMBDA_KICK_URL?.trim();
  return configuredUrl ? configuredUrl : null;
};

export const kickLambda = async (env: Env, actionId: string): Promise<void> => {
  const kickUrl = getLambdaKickUrl(env);
  if (!kickUrl) {
    throw new Error("LAMBDA_KICK_URL is not configured");
  }

  const kickSecret = env.INTERNAL_KICK_SECRET?.trim();
  if (!kickSecret) {
    throw new Error("INTERNAL_KICK_SECRET is required when LAMBDA_KICK_URL is configured");
  }

  const response = await fetch(kickUrl, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      "x-internal-kick-secret": kickSecret
    },
    body: JSON.stringify({
      reason: "webhook_enqueue",
      actionId
    })
  });

  if (!response.ok) {
    throw new Error(`Lambda kick failed with status ${response.status}`);
  }
};

// ── Route handlers ───────────────────────────────────────────────────────────

async function handleTrelloWebhook(
  request: Request,
  env: Env,
  deps: WebhookDependencies
): Promise<Response> {
  const bodyText = await request.text();

  const isValid = await verifyTrelloWebhook(
    bodyText,
    request.headers.get("x-trello-webhook"),
    env.TRELLO_WEBHOOK_CALLBACK_URL,
    env.TRELLO_API_SECRET
  );

  if (!isValid) {
    return jsonResponse(401, { error: "Invalid webhook signature" });
  }

  let payload: TrelloWebhookPayload;
  try {
    payload = JSON.parse(bodyText) as TrelloWebhookPayload;
  } catch {
    return jsonResponse(400, { error: "Invalid JSON payload" });
  }

  const actionId = extractActionId(payload);
  if (!actionId) {
    return jsonResponse(400, { error: "Missing action.id" });
  }

  const cardId = extractCardId(payload);
  if (!cardId) {
    console.log(JSON.stringify({
      message: "Webhook ignored — no card in event",
      actionId, actionType: payload.action?.type ?? "unknown"
    }));
    return jsonResponse(200, { accepted: false, reason: "no_card", actionId });
  }

  const sql = neon(env.NEON_DATABASE_URL) as SqlFunction;

  // Enqueue lightweight ping to the pings queue
  const pingQueue = env.PGMQ_PING_QUEUE_NAME ?? "pings";
  try {
    const messageId = await deps.enqueuePing(sql, pingQueue, "trello");
    console.log(JSON.stringify({
      message: "Ping enqueued (trello)", actionId, cardId, messageId
    }));
  } catch (error) {
    console.error(JSON.stringify({
      message: "Ping enqueue failed", actionId, cardId,
      error: error instanceof Error ? error.message : "unknown_error"
    }));
    return jsonResponse(500, { error: "Failed to enqueue ping" });
  }

  // Optional: kick downstream consumer
  const lambdaKickUrl = getLambdaKickUrl(env);
  if (lambdaKickUrl) {
    try {
      await deps.kickLambda(env, actionId);
      console.log(JSON.stringify({ message: "Lambda kick succeeded", actionId }));
    } catch (error) {
      console.error(JSON.stringify({
        message: "Lambda kick failed", actionId,
        error: error instanceof Error ? error.message : "unknown_error"
      }));
    }
  }

  return jsonResponse(200, { accepted: true, actionId, source: "trello" });
}

async function handleGitHubWebhook(
  request: Request,
  env: Env,
  deps: WebhookDependencies
): Promise<Response> {
  const secret = env.GITHUB_WEBHOOK_SECRET?.trim();
  if (!secret || secret.length === 0) {
    return jsonResponse(500, { error: "GITHUB_WEBHOOK_SECRET not configured" });
  }

  const bodyText = await request.text();

  const isValid = await verifyGitHubWebhook(
    bodyText,
    request.headers.get("x-hub-signature-256"),
    secret
  );

  if (!isValid) {
    return jsonResponse(401, { error: "Invalid webhook signature" });
  }

  // GitHub sends a "ping" event when the webhook is first registered
  const eventType = request.headers.get("x-github-event");
  if (eventType === "ping") {
    return jsonResponse(200, { accepted: true, event: "ping", message: "Webhook registered" });
  }

  // Only process project item status changes — the primary signal that a card moved columns.
  // "issues" events are intentionally excluded: they fire for every label/assignee/comment change
  // which would generate excessive pings. The board re-query catches issue-level changes.
  if (eventType !== "projects_v2_item") {
    console.log(JSON.stringify({
      message: "GitHub webhook ignored — irrelevant event", eventType
    }));
    return jsonResponse(200, { accepted: false, reason: "irrelevant_event", eventType });
  }

  // Filter by action — only "edited" (field value changed, e.g. status) triggers a ping.
  // "created"/"deleted"/"archived"/"restored" are less relevant for column-move detection.
  let body: Record<string, unknown>;
  try {
    body = JSON.parse(bodyText) as Record<string, unknown>;
  } catch {
    return jsonResponse(400, { error: "Invalid JSON payload" });
  }

  const action = typeof body.action === "string" ? body.action : null;
  if (action !== "edited") {
    console.log(JSON.stringify({
      message: "GitHub webhook ignored — non-edit action", eventType, action
    }));
    return jsonResponse(200, { accepted: false, reason: "non_edit_action", eventType, action });
  }

  const sql = neon(env.NEON_DATABASE_URL) as SqlFunction;
  const pingQueue = env.PGMQ_PING_QUEUE_NAME ?? "pings";

  try {
    const messageId = await deps.enqueuePing(sql, pingQueue, "github");
    console.log(JSON.stringify({
      message: "Ping enqueued (github)", eventType, messageId
    }));
  } catch (error) {
    console.error(JSON.stringify({
      message: "Ping enqueue failed (github)", eventType,
      error: error instanceof Error ? error.message : "unknown_error"
    }));
    return jsonResponse(500, { error: "Failed to enqueue ping" });
  }

  return jsonResponse(200, { accepted: true, source: "github", eventType });
}

// ── Main handler ─────────────────────────────────────────────────────────────

export const createWebhookHandler = (dependencies: WebhookDependencies) => ({
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    if (request.method === "GET" && url.pathname === "/health") {
      return jsonResponse(200, { ok: true, service: "task-board-webhook-worker" });
    }

    // Trello sends HEAD to verify the webhook callback URL exists
    if (request.method === "HEAD" && url.pathname === "/webhooks/trello") {
      return new Response(null, { status: 200 });
    }

    if (request.method === "POST" && url.pathname === "/webhooks/trello") {
      return handleTrelloWebhook(request, env, dependencies);
    }

    if (request.method === "POST" && url.pathname === "/webhooks/github") {
      return handleGitHubWebhook(request, env, dependencies);
    }

    return jsonResponse(404, { error: "Not found" });
  }
});

export default createWebhookHandler({
  enqueuePing,
  enqueueWebhookEvent,
  kickLambda
});
