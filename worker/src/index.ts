import { neon } from "@neondatabase/serverless";

export interface Env {
  TRELLO_API_SECRET: string;
  TRELLO_WEBHOOK_CALLBACK_URL: string;
  NEON_DATABASE_URL: string;
  PGMQ_QUEUE_NAME?: string;
  LAMBDA_KICK_URL?: string;
  INTERNAL_KICK_SECRET?: string;
}

interface TrelloWebhookPayload {
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

const verifyTrelloWebhook = async (
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

  // Constant-time comparison to prevent timing attacks
  if (expectedSignature.length !== signatureHeader.length) return false;
  const a = encoder.encode(expectedSignature);
  const b = encoder.encode(signatureHeader);
  let mismatch = 0;
  for (let i = 0; i < a.length; i++) {
    mismatch |= a[i] ^ b[i];
  }
  return mismatch === 0;
};

const extractActionId = (payload: TrelloWebhookPayload): string | null =>
  payload.action?.id ?? null;

const extractCardId = (payload: TrelloWebhookPayload): string | null =>
  payload.action?.data?.card?.id ?? null;

const getLambdaKickUrl = (env: Env): string | null => {
  const configuredUrl = env.LAMBDA_KICK_URL?.trim();
  return configuredUrl ? configuredUrl : null;
};

const enqueueWebhookEvent = async (
  env: Env,
  actionId: string,
  cardId: string | null,
  payload: TrelloWebhookPayload
): Promise<number> => {
  const queueName = env.PGMQ_QUEUE_NAME ?? "events";
  const sql = neon(env.NEON_DATABASE_URL);

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

  const messageId = (rows as Array<{ message_id: unknown }>)[0]?.message_id;

  if (typeof messageId !== "number") {
    throw new Error("PGMQ enqueue did not return a message_id");
  }

  return messageId;
};

const kickLambda = async (env: Env, actionId: string): Promise<void> => {
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

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    if (request.method === "GET" && url.pathname === "/health") {
      return jsonResponse(200, { ok: true, service: "task-board-webhook-worker" });
    }

    // Trello sends HEAD to verify the webhook callback URL exists
    if (request.method === "HEAD" && url.pathname === "/webhooks/trello") {
      return new Response(null, { status: 200 });
    }

    if (request.method !== "POST" || url.pathname !== "/webhooks/trello") {
      return jsonResponse(404, { error: "Not found" });
    }

    // Read body as text first — needed for HMAC signature verification
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

    // Only process events that involve a card — ignore board/list/member events
    const cardId = extractCardId(payload);

    if (!cardId) {
      console.log(
        JSON.stringify({
          message: "Webhook ignored — no card in event",
          actionId,
          actionType: payload.action?.type ?? "unknown"
        })
      );
      return jsonResponse(200, { accepted: false, reason: "no_card", actionId });
    }

    let messageId: number;

    try {
      messageId = await enqueueWebhookEvent(env, actionId, cardId, payload);
    } catch (error) {
      console.error(
        JSON.stringify({
          message: "Webhook enqueue failed",
          actionId,
          cardId,
          error: error instanceof Error ? error.message : "unknown_error"
        })
      );

      return jsonResponse(500, { error: "Failed to enqueue webhook event" });
    }

    console.log(
      JSON.stringify({
        message: "Webhook accepted",
        actionId,
        cardId,
        messageId,
        queueBackend: "pgmq"
      })
    );

    const lambdaKickUrl = getLambdaKickUrl(env);

    if (!lambdaKickUrl) {
      console.log(
        JSON.stringify({
          message: "Lambda kick skipped",
          actionId,
          kickStatus: "skipped"
        })
      );
    } else {
      try {
        await kickLambda(env, actionId);
        console.log(
          JSON.stringify({
            message: "Lambda kick succeeded",
            actionId,
            kickStatus: "succeeded"
          })
        );
      } catch (error) {
        console.error(
          JSON.stringify({
            message: "Lambda kick failed",
            actionId,
            kickStatus: "failed",
            error: error instanceof Error ? error.message : "unknown_error"
          })
        );
      }
    }

    return jsonResponse(200, { accepted: true, actionId });
  }
};
