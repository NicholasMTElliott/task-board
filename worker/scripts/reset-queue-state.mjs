import { neon } from "@neondatabase/serverless";

const connectionString = process.env.NEON_DATABASE_URL;
const queueName = process.env.PGMQ_QUEUE_NAME ?? "events";
const actionIdPrefix = process.env.RESET_ACTION_ID_PREFIX ?? "act_";
const includeAll = (process.env.RESET_INCLUDE_ALL ?? "false").toLowerCase() === "true";

if (!connectionString) {
  throw new Error("NEON_DATABASE_URL is required");
}

if (!/^[a-zA-Z0-9_]+$/.test(queueName)) {
  throw new Error(`Invalid queue name '${queueName}'. Only letters, numbers, and underscore are allowed.`);
}

const sql = neon(connectionString);
const queueTable = `pgmq.q_${queueName}`;
const archiveTable = `pgmq.a_${queueName}`;

const deleteScope = includeAll
  ? {
      queueQuery: `with deleted as (delete from ${queueTable} returning 1) select count(*)::int as count from deleted`,
      archiveQuery: `with deleted as (delete from ${archiveTable} returning 1) select count(*)::int as count from deleted`,
      processedQuery: "with deleted as (delete from processed_events returning 1) select count(*)::int as count from deleted"
    }
  : {
      queueQuery: `with deleted as (delete from ${queueTable} where message->>'actionId' like $1 returning 1) select count(*)::int as count from deleted`,
      archiveQuery: `with deleted as (delete from ${archiveTable} where message->>'actionId' like $1 returning 1) select count(*)::int as count from deleted`,
      processedQuery: "with deleted as (delete from processed_events where action_id like $1 returning 1) select count(*)::int as count from deleted"
    };

const filterValue = `${actionIdPrefix}%`;

const queueResult = includeAll
  ? await sql(deleteScope.queueQuery)
  : await sql(deleteScope.queueQuery, [filterValue]);

const archiveResult = includeAll
  ? await sql(deleteScope.archiveQuery)
  : await sql(deleteScope.archiveQuery, [filterValue]);

const processedResult = includeAll
  ? await sql(deleteScope.processedQuery)
  : await sql(deleteScope.processedQuery, [filterValue]);

const result = {
  queueName,
  includeAll,
  actionIdPrefix,
  removed: {
    queue: queueResult[0]?.count ?? 0,
    archive: archiveResult[0]?.count ?? 0,
    processedEvents: processedResult[0]?.count ?? 0
  }
};

console.log(JSON.stringify(result));
