import { neon } from "@neondatabase/serverless";

const connectionString = process.env.NEON_DATABASE_URL;
const actionId = process.env.ACTION_ID;
const queueName = process.env.PGMQ_QUEUE_NAME ?? "events";

if (!connectionString) {
  throw new Error("NEON_DATABASE_URL is required");
}

if (!actionId) {
  throw new Error("ACTION_ID is required");
}

if (!/^[a-zA-Z0-9_]+$/.test(queueName)) {
  throw new Error(`Invalid queue name '${queueName}'. Only letters, numbers, and underscore are allowed.`);
}

const sql = neon(connectionString);
const queueTable = `pgmq.q_${queueName}`;

const queueRows = await sql(
  `select msg_id, message from ${queueTable} where message->>'actionId' = $1 order by msg_id desc limit 1`,
  [actionId]
);

const processedRows = await sql(
  "select action_id, processed_at_utc from processed_events where action_id = $1 order by processed_at_utc desc limit 1",
  [actionId]
);

const result = {
  actionId,
  queueName,
  queueFound: queueRows.length > 0,
  queueMessageId: queueRows[0]?.msg_id ?? null,
  processedFound: processedRows.length > 0,
  processedAtUtc: processedRows[0]?.processed_at_utc ?? null
};

console.log(JSON.stringify(result));
