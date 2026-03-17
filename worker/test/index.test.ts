import { describe, it, expect, vi, beforeAll, afterAll } from "vitest";
import { GenericContainer, type StartedTestContainer } from "testcontainers";
import { Client, types as pgTypes } from "pg";
import { readFileSync } from "fs";
import { join } from "path";
import {
  verifyTrelloWebhook,
  extractActionId,
  extractCardId,
  enqueueWebhookEvent,
  kickLambda,
  getLambdaKickUrl,
  type Env,
  type SqlFunction,
  type TrelloWebhookPayload,
} from "../src/index";

// Parse PostgreSQL bigint (OID 20) as JavaScript number instead of string.
// pgmq.send() returns bigint, and our production code (neon driver) returns numbers.
pgTypes.setTypeParser(20, (val: string) => Number(val));

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

const MIGRATIONS_DIR = join(__dirname, "..", "..", "db", "migrations");

/** Read a migration SQL file by filename. */
const readMigration = (filename: string): string =>
  readFileSync(join(MIGRATIONS_DIR, filename), "utf-8");

/**
 * Build a tagged-template SQL function backed by a real pg Client.
 * Mimics the neon() return type: tagged template → Promise<Record[]>.
 */
function buildSqlFunction(client: Client): SqlFunction {
  return async (
    strings: TemplateStringsArray,
    ...values: unknown[]
  ): Promise<Record<string, unknown>[]> => {
    // Build a parameterised query from the tagged template
    let text = "";
    for (let i = 0; i < strings.length; i++) {
      text += strings[i];
      if (i < values.length) {
        text += `$${i + 1}`;
      }
    }
    const result = await client.query(text, values);
    return result.rows;
  };
}

/** Produce a valid HMAC-SHA1 signature for the given body + callbackUrl. */
async function signPayload(
  body: string,
  callbackUrl: string,
  secret: string
): Promise<string> {
  const encoder = new TextEncoder();
  const key = await crypto.subtle.importKey(
    "raw",
    encoder.encode(secret),
    { name: "HMAC", hash: "SHA-1" },
    false,
    ["sign"]
  );
  const signed = await crypto.subtle.sign(
    "HMAC",
    key,
    encoder.encode(body + callbackUrl)
  );
  return btoa(String.fromCharCode(...new Uint8Array(signed)));
}

// ---------------------------------------------------------------------------
// HMAC verification
// ---------------------------------------------------------------------------

describe("verifyTrelloWebhook", () => {
  const secret = "test-api-secret";
  const callbackUrl = "https://example.com/webhooks/trello";
  const body = '{"action":{"id":"abc123"}}';

  it("returns true for a valid signature", async () => {
    const sig = await signPayload(body, callbackUrl, secret);
    const result = await verifyTrelloWebhook(body, sig, callbackUrl, secret);
    expect(result).toBe(true);
  });

  it("returns false for an invalid signature", async () => {
    const result = await verifyTrelloWebhook(
      body,
      "badsignature",
      callbackUrl,
      secret
    );
    expect(result).toBe(false);
  });

  it("returns false when signature header is null", async () => {
    const result = await verifyTrelloWebhook(body, null, callbackUrl, secret);
    expect(result).toBe(false);
  });
});

// ---------------------------------------------------------------------------
// Extraction helpers
// ---------------------------------------------------------------------------

describe("extractActionId", () => {
  it("returns action.id when present", () => {
    const payload: TrelloWebhookPayload = { action: { id: "act_123" } };
    expect(extractActionId(payload)).toBe("act_123");
  });

  it("returns null when action is missing", () => {
    expect(extractActionId({})).toBeNull();
  });

  it("returns null when action.id is undefined", () => {
    expect(extractActionId({ action: {} })).toBeNull();
  });
});

describe("extractCardId", () => {
  it("returns card.id when present", () => {
    const payload: TrelloWebhookPayload = {
      action: { id: "a1", data: { card: { id: "card_456" } } },
    };
    expect(extractCardId(payload)).toBe("card_456");
  });

  it("returns null for non-card event", () => {
    const payload: TrelloWebhookPayload = {
      action: { id: "a1", data: {} },
    };
    expect(extractCardId(payload)).toBeNull();
  });

  it("returns null when data is missing", () => {
    const payload: TrelloWebhookPayload = { action: { id: "a1" } };
    expect(extractCardId(payload)).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// getLambdaKickUrl
// ---------------------------------------------------------------------------

describe("getLambdaKickUrl", () => {
  it("returns trimmed URL when configured", () => {
    const env = { LAMBDA_KICK_URL: " https://kick.example.com " } as Env;
    expect(getLambdaKickUrl(env)).toBe("https://kick.example.com");
  });

  it("returns null when empty", () => {
    const env = { LAMBDA_KICK_URL: "" } as Env;
    expect(getLambdaKickUrl(env)).toBeNull();
  });

  it("returns null when undefined", () => {
    const env = {} as Env;
    expect(getLambdaKickUrl(env)).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// Enqueue (real Postgres + PGMQ)
// ---------------------------------------------------------------------------

describe("enqueueWebhookEvent (real Postgres)", () => {
  let container: StartedTestContainer;
  let client: Client;
  let sql: SqlFunction;

  beforeAll(async () => {
    container = await new GenericContainer("postgres:16")
      .withExposedPorts(5432)
      .withEnvironment({
        POSTGRES_USER: "test",
        POSTGRES_PASSWORD: "test",
        POSTGRES_DB: "test",
      })
      .start();

    client = new Client({
      host: container.getHost(),
      port: container.getMappedPort(5432),
      user: "test",
      password: "test",
      database: "test",
    });

    await client.connect();

    // Apply migrations needed for PGMQ
    await client.query(readMigration("V1__processed_events.sql"));
    await client.query(readMigration("V2__pgmq_core.sql"));
    await client.query(readMigration("V3__pgmq_create_events_queue.sql"));

    sql = buildSqlFunction(client);
  });

  afterAll(async () => {
    await client?.end();
    await container?.stop();
  });

  it("inserts a message with the correct payload shape", async () => {
    const payload: TrelloWebhookPayload = {
      action: { id: "act_1", data: { card: { id: "card_1" } } },
    };

    const messageId = await enqueueWebhookEvent(
      sql,
      "events",
      "act_1",
      "card_1",
      payload
    );

    expect(typeof messageId).toBe("number");

    // Read the message directly from the queue table and verify shape
    const result = await client.query(
      `SELECT message FROM pgmq.q_events WHERE msg_id = $1`,
      [messageId]
    );
    expect(result.rows).toHaveLength(1);

    const stored = result.rows[0].message;
    expect(stored).toHaveProperty("actionId", "act_1");
    expect(stored).toHaveProperty("cardId", "card_1");
    expect(stored).toHaveProperty("receivedAtUtc");
    expect(stored).toHaveProperty("payload");
    expect(stored.payload.action.id).toBe("act_1");
  });

  it("returns a numeric messageId", async () => {
    const messageId = await enqueueWebhookEvent(sql, "events", "act_2", null, {
      action: { id: "act_2" },
    });
    expect(Number.isInteger(messageId)).toBe(true);
    expect(messageId).toBeGreaterThan(0);
  });

  it("payload is readable via pgmq.read()", async () => {
    const payload: TrelloWebhookPayload = {
      action: { id: "act_3", data: { card: { id: "card_3" } } },
    };

    await enqueueWebhookEvent(sql, "events", "act_3", "card_3", payload);

    // Read a batch large enough to include our message (earlier tests may have added messages)
    const readResult = await client.query(
      `SELECT * FROM pgmq.read('events', 30, 100)`
    );
    expect(readResult.rows.length).toBeGreaterThanOrEqual(1);

    const msg = readResult.rows.find(
      (r: Record<string, unknown>) => (r.message as Record<string, unknown>).actionId === "act_3"
    );
    expect(msg).toBeDefined();
    expect(msg!.message.cardId).toBe("card_3");
    expect(msg!.message.payload.action.data.card.id).toBe("card_3");
  });

  it("stores null cardId correctly", async () => {
    const payload: TrelloWebhookPayload = {
      action: { id: "act_4", type: "updateBoard" },
    };

    const messageId = await enqueueWebhookEvent(
      sql,
      "events",
      "act_4",
      null,
      payload
    );

    const result = await client.query(
      `SELECT message FROM pgmq.q_events WHERE msg_id = $1`,
      [messageId]
    );
    const stored = result.rows[0].message;
    expect(stored.cardId).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// kickLambda (mocked fetch)
// ---------------------------------------------------------------------------

describe("kickLambda", () => {
  const baseEnv: Env = {
    TRELLO_API_SECRET: "secret",
    TRELLO_WEBHOOK_CALLBACK_URL: "https://cb.example.com",
    NEON_DATABASE_URL: "postgresql://unused",
    LAMBDA_KICK_URL: "https://kick.example.com/drain",
    INTERNAL_KICK_SECRET: "kick-secret-123",
  };

  it("sends correct headers and body", async () => {
    const mockFetch = vi.fn().mockResolvedValue({ ok: true });
    vi.stubGlobal("fetch", mockFetch);

    await kickLambda(baseEnv, "action_99");

    expect(mockFetch).toHaveBeenCalledOnce();
    const [url, init] = mockFetch.mock.calls[0];
    expect(url).toBe("https://kick.example.com/drain");
    expect(init.method).toBe("POST");
    expect(init.headers["x-internal-kick-secret"]).toBe("kick-secret-123");

    const body = JSON.parse(init.body);
    expect(body).toEqual({ reason: "webhook_enqueue", actionId: "action_99" });

    vi.unstubAllGlobals();
  });

  it("throws when LAMBDA_KICK_URL is not configured", async () => {
    const env = { ...baseEnv, LAMBDA_KICK_URL: "" };
    await expect(kickLambda(env, "a1")).rejects.toThrow(
      "LAMBDA_KICK_URL is not configured"
    );
  });

  it("throws on non-ok response", async () => {
    const mockFetch = vi.fn().mockResolvedValue({ ok: false, status: 503 });
    vi.stubGlobal("fetch", mockFetch);

    await expect(kickLambda(baseEnv, "a1")).rejects.toThrow(
      "Lambda kick failed with status 503"
    );

    vi.unstubAllGlobals();
  });
});

// ---------------------------------------------------------------------------
// HTTP handler integration (real DB + mock fetch)
// ---------------------------------------------------------------------------

// vi.hoisted runs before vi.mock hoisting, so we can create a mutable ref
// that the mock factory captures and we populate later in beforeAll.
const neonState = vi.hoisted(() => {
  let currentSql: SqlFunction | null = null;
  return {
    setSql: (fn: SqlFunction) => { currentSql = fn; },
    neon: () => {
      if (!currentSql) throw new Error("sql function not initialised");
      return currentSql;
    },
  };
});

vi.mock("@neondatabase/serverless", () => ({
  neon: (..._args: unknown[]) => neonState.neon(),
}));

describe("HTTP handler (integration)", () => {
  let container: StartedTestContainer;
  let client: Client;

  const secret = "test-trello-secret";
  const callbackUrl = "https://example.com/webhooks/trello";

  let handler: { fetch: (request: Request, env: Env) => Promise<Response> };

  beforeAll(async () => {
    container = await new GenericContainer("postgres:16")
      .withExposedPorts(5432)
      .withEnvironment({
        POSTGRES_USER: "test",
        POSTGRES_PASSWORD: "test",
        POSTGRES_DB: "test",
      })
      .start();

    client = new Client({
      host: container.getHost(),
      port: container.getMappedPort(5432),
      user: "test",
      password: "test",
      database: "test",
    });

    await client.connect();
    await client.query(readMigration("V1__processed_events.sql"));
    await client.query(readMigration("V2__pgmq_core.sql"));
    await client.query(readMigration("V3__pgmq_create_events_queue.sql"));

    neonState.setSql(buildSqlFunction(client));

    // Import the handler — the module-level vi.mock has already replaced neon
    const mod = await import("../src/index");
    handler = mod.default;
  });

  afterAll(async () => {
    await client?.end();
    await container?.stop();
  });

  function makeEnv(overrides: Partial<Env> = {}): Env {
    return {
      TRELLO_API_SECRET: secret,
      TRELLO_WEBHOOK_CALLBACK_URL: callbackUrl,
      NEON_DATABASE_URL: "postgresql://unused",
      PGMQ_QUEUE_NAME: "events",
      ...overrides,
    };
  }

  async function makeSignedRequest(
    body: string,
    method = "POST",
    path = "/webhooks/trello"
  ): Promise<Request> {
    const sig = await signPayload(body, callbackUrl, secret);
    return new Request(`https://worker.test${path}`, {
      method,
      headers: {
        "content-type": "application/json",
        "x-trello-webhook": sig,
      },
      body: method !== "HEAD" ? body : undefined,
    });
  }

  it("HEAD /webhooks/trello returns 200", async () => {
    const req = new Request("https://worker.test/webhooks/trello", {
      method: "HEAD",
    });
    const res = await handler.fetch(req, makeEnv());
    expect(res.status).toBe(200);
  });

  it("POST with valid card webhook returns 200 and enqueues", async () => {
    // Stub fetch for Lambda kick (no kick URL configured, so it won't fire)
    const env = makeEnv();
    const body = JSON.stringify({
      action: { id: "int_act_1", data: { card: { id: "int_card_1" } } },
    });
    const req = await makeSignedRequest(body);

    const res = await handler.fetch(req, env);
    const json = await res.json();

    expect(res.status).toBe(200);
    expect(json).toMatchObject({ accepted: true, actionId: "int_act_1" });

    // Verify the message landed in the queue
    const dbResult = await client.query(
      `SELECT message FROM pgmq.q_events ORDER BY msg_id DESC LIMIT 1`
    );
    expect(dbResult.rows[0].message.actionId).toBe("int_act_1");
  });

  it("POST with invalid signature returns 401", async () => {
    const req = new Request("https://worker.test/webhooks/trello", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-trello-webhook": "invalidsig",
      },
      body: '{"action":{"id":"x"}}',
    });

    const res = await handler.fetch(req, makeEnv());
    expect(res.status).toBe(401);
  });

  it("POST with non-card event returns 200 accepted:false", async () => {
    const body = JSON.stringify({
      action: { id: "board_act_1", type: "updateBoard", data: {} },
    });
    const req = await makeSignedRequest(body);

    const res = await handler.fetch(req, makeEnv());
    const json = await res.json();

    expect(res.status).toBe(200);
    expect(json).toMatchObject({
      accepted: false,
      reason: "no_card",
      actionId: "board_act_1",
    });
  });
});
