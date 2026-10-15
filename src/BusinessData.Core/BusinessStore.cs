using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BusinessData.Core;

/// <summary>Small, single-tenant SQLite reference store. No network or payment side effects.</summary>
public sealed class BusinessStore
{
    private readonly string connectionString;
    private readonly string databasePath;
    private readonly TimeProvider clock;
    private static readonly HashSet<string> Reasons = ["damaged", "wrong_item", "no_longer_needed"];

    public BusinessStore(string path, TimeProvider? clock = null)
    {
        var fullPath = Path.GetFullPath(path);
        databasePath = fullPath;
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath, ForeignKeys = true, DefaultTimeout = 5
        }.ToString();
        this.clock = clock ?? TimeProvider.System;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? tx = null,
        params (string Name, object? Value)[] parameters)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public void Initialize()
    {
        using var db = Open();
        using var cmd = Command(db, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS customers(id TEXT PRIMARY KEY, name TEXT NOT NULL, tier TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS orders(
                id TEXT PRIMARY KEY, customer_id TEXT NOT NULL REFERENCES customers(id),
                status TEXT NOT NULL, total_cents INTEGER NOT NULL CHECK(total_cents >= 0),
                currency TEXT NOT NULL, placed_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS return_intents(
                id TEXT PRIMARY KEY, actor TEXT NOT NULL, order_id TEXT NOT NULL REFERENCES orders(id),
                reason TEXT NOT NULL, expires_at TEXT NOT NULL, approved_at TEXT, consumed_at TEXT,
                preparation_key TEXT NOT NULL, UNIQUE(actor, preparation_key));
            CREATE TABLE IF NOT EXISTS returns(
                id TEXT PRIMARY KEY, actor TEXT NOT NULL, order_id TEXT NOT NULL UNIQUE REFERENCES orders(id),
                intent_id TEXT NOT NULL UNIQUE REFERENCES return_intents(id),
                idempotency_key TEXT NOT NULL, status TEXT NOT NULL, created_at TEXT NOT NULL,
                UNIQUE(actor, idempotency_key));
            CREATE TABLE IF NOT EXISTS audit(
                id TEXT PRIMARY KEY, timestamp TEXT NOT NULL, actor TEXT NOT NULL, tool TEXT NOT NULL,
                arguments TEXT NOT NULL, outcome TEXT NOT NULL, result_code TEXT, duration_ms INTEGER);
            """);
        cmd.ExecuteNonQuery();
        if (!OperatingSystem.IsWindows())
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(databasePath + suffix))
                    File.SetUnixFileMode(databasePath + suffix, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        using var tx = db.BeginTransaction();
        using var seed = Command(db, """
            INSERT OR IGNORE INTO customers VALUES
                ('CUST-001','Avery Morgan','gold'), ('CUST-002','Jordan Lee','standard'),
                ('CUST-003','Taylor Quinn','standard');
            INSERT OR IGNORE INTO orders VALUES
                ('ORD-1001','CUST-001','delivered',12990,'USD','2026-10-01T09:00:00Z'),
                ('ORD-1002','CUST-001','shipped',7990,'USD','2026-10-12T10:00:00Z'),
                ('ORD-1003','CUST-002','delivered',24900,'USD','2026-10-03T12:00:00Z'),
                ('ORD-1004','CUST-003','cancelled',4500,'USD','2026-10-05T08:00:00Z');
            """, tx);
        seed.ExecuteNonQuery();
        tx.Commit();
    }

    private const string OrderSelect = """
        SELECT o.id,o.customer_id,c.name,o.status,o.total_cents,o.currency,o.placed_at
        FROM orders o JOIN customers c ON c.id=o.customer_id
        """;
    private static Order ReadOrder(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5), r.GetString(6));
    private static ReturnIntent ReadIntent(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6));
    private static ReturnTicket ReadReturn(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));

    public IReadOnlyList<Order> SearchOrders(string query, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 80 || limit is < 1 or > 20)
            throw new BusinessException("invalid_argument", "Use a query of 1–80 characters and a limit of 1–20.");
        var escaped = query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        using var db = Open();
        using var cmd = Command(db, OrderSelect + """
             WHERE o.id LIKE @q ESCAPE '\' OR c.name LIKE @q ESCAPE '\'
             ORDER BY o.id LIMIT @limit
            """, null, ("@q", $"%{escaped}%"), ("@limit", limit));
        using var r = cmd.ExecuteReader();
        var results = new List<Order>();
        while (r.Read()) results.Add(ReadOrder(r));
        return results;
    }

    public Order GetOrder(string orderId)
    {
        ValidateId(orderId, "ORD-");
        using var db = Open();
        return GetOrder(db, orderId);
    }

    private static Order GetOrder(SqliteConnection db, string id, SqliteTransaction? tx = null)
    {
        using var cmd = Command(db, OrderSelect + " WHERE o.id=@id", tx, ("@id", id));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new BusinessException("not_found", "Order not found.");
        return ReadOrder(r);
    }

    public CustomerHistory GetCustomerHistory(string customerId)
    {
        ValidateId(customerId, "CUST-");
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        Customer customer;
        using (var cmd = Command(db, "SELECT id,name,tier FROM customers WHERE id=@id", tx, ("@id", customerId)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) throw new BusinessException("not_found", "Customer not found.");
            customer = new(r.GetString(0), r.GetString(1), r.GetString(2));
        }
        var orders = new List<Order>();
        using (var cmd = Command(db, OrderSelect + " WHERE o.customer_id=@id ORDER BY o.placed_at DESC LIMIT 20", tx, ("@id", customerId)))
        using (var r = cmd.ExecuteReader()) while (r.Read()) orders.Add(ReadOrder(r));
        var returns = new List<ReturnTicket>();
        using (var cmd = Command(db, """
            SELECT r.id,r.order_id,r.intent_id,r.status,r.created_at FROM returns r
            JOIN orders o ON o.id=r.order_id WHERE o.customer_id=@id ORDER BY r.created_at DESC LIMIT 20
            """, tx, ("@id", customerId)))
        using (var r = cmd.ExecuteReader()) while (r.Read()) returns.Add(ReadReturn(r));
        tx.Commit();
        return new(customer, orders, returns);
    }

    public ReturnIntent PrepareReturn(string actor, string orderId, string reason, string idempotencyKey)
    {
        ValidateId(orderId, "ORD-");
        ValidateKey(idempotencyKey);
        if (!Reasons.Contains(reason)) throw new BusinessException("invalid_argument", "Reason must be damaged, wrong_item or no_longer_needed.");
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using (var previous = Command(db, "SELECT id,actor,order_id,reason,expires_at,approved_at,consumed_at FROM return_intents WHERE actor=@actor AND preparation_key=@key",
                   tx, ("@actor", actor), ("@key", idempotencyKey)))
        using (var r = previous.ExecuteReader())
        {
            if (r.Read())
            {
                var existing = ReadIntent(r);
                if (existing.OrderId != orderId || existing.Reason != reason) throw Conflict();
                return existing;
            }
        }
        if (GetOrder(db, orderId, tx).Status != "delivered")
            throw new BusinessException("ineligible_order", "Only delivered orders can be returned.");
        using (var exists = Command(db, "SELECT count(*) FROM returns WHERE order_id=@id", tx, ("@id", orderId)))
            if ((long)exists.ExecuteScalar()! != 0) throw new BusinessException("already_returned", "This order already has a return ticket.");
        var intent = new ReturnIntent("RI-" + Guid.NewGuid().ToString("N"), actor, orderId, reason,
            Stamp(clock.GetUtcNow().AddMinutes(10)), null, null);
        using var insert = Command(db, """
            INSERT INTO return_intents(id,actor,order_id,reason,expires_at,preparation_key)
            VALUES(@id,@actor,@order,@reason,@expiry,@key)
            """, tx, ("@id", intent.Id), ("@actor", actor), ("@order", orderId), ("@reason", reason),
            ("@expiry", intent.ExpiresAt), ("@key", idempotencyKey));
        insert.ExecuteNonQuery();
        tx.Commit();
        return intent;
    }

    public ReturnIntent GetIntent(string intentId)
    {
        ValidateId(intentId, "RI-");
        using var db = Open();
        return GetIntent(db, intentId);
    }

    private static ReturnIntent GetIntent(SqliteConnection db, string id, SqliteTransaction? tx = null)
    {
        using var cmd = Command(db, "SELECT id,actor,order_id,reason,expires_at,approved_at,consumed_at FROM return_intents WHERE id=@id", tx, ("@id", id));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new BusinessException("not_found", "Return intent not found.");
        return ReadIntent(r);
    }

    // Deliberately NOT exposed as an MCP tool. The local operator CLI is the only adapter.
    public void ApproveReturn(string intentId)
    {
        ValidateId(intentId, "RI-");
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var intent = GetIntent(db, intentId, tx);
        CheckExpiry(intent);
        if (intent.ConsumedAt != null) throw new BusinessException("already_consumed", "Intent has already been consumed.");
        using var cmd = Command(db, "UPDATE return_intents SET approved_at=COALESCE(approved_at,@now) WHERE id=@id", tx,
            ("@now", Stamp(clock.GetUtcNow())), ("@id", intentId));
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public ReturnTicket RequestReturn(string actor, string intentId, string idempotencyKey)
    {
        ValidateId(intentId, "RI-");
        ValidateKey(idempotencyKey);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using (var cmd = Command(db, "SELECT id,order_id,intent_id,status,created_at FROM returns WHERE actor=@actor AND idempotency_key=@key", tx,
                   ("@actor", actor), ("@key", idempotencyKey)))
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read())
            {
                var previous = ReadReturn(r);
                if (previous.IntentId != intentId) throw Conflict();
                return previous; // A retry stays safe even after the approval expires.
            }
        }
        var intent = GetIntent(db, intentId, tx);
        if (intent.Actor != actor) throw new BusinessException("forbidden", "Intent belongs to another principal.");
        CheckExpiry(intent);
        if (intent.ApprovedAt == null) throw new BusinessException("confirmation_required", "A human operator must approve this intent locally before submission.");
        if (intent.ConsumedAt != null) throw new BusinessException("already_consumed", "Use the original idempotency key to replay the result.");
        if (GetOrder(db, intent.OrderId, tx).Status != "delivered")
            throw new BusinessException("ineligible_order", "Only delivered orders can be returned.");
        var ticket = new ReturnTicket("RET-" + Guid.NewGuid().ToString("N"), intent.OrderId, intentId,
            "requested", Stamp(clock.GetUtcNow()));
        using var insert = Command(db, """
            INSERT INTO returns(id,actor,order_id,intent_id,idempotency_key,status,created_at)
            VALUES(@id,@actor,@order,@intent,@key,'requested',@now);
            UPDATE return_intents SET consumed_at=@now WHERE id=@intent;
            """, tx, ("@id", ticket.Id), ("@actor", actor), ("@order", ticket.OrderId), ("@intent", intentId),
            ("@key", idempotencyKey), ("@now", ticket.CreatedAt));
        try { insert.ExecuteNonQuery(); }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            throw new BusinessException("already_returned", "This order already has a return ticket.");
        }
        tx.Commit();
        return ticket;
    }

    private void CheckExpiry(ReturnIntent intent)
    {
        if (DateTimeOffset.Parse(intent.ExpiresAt, CultureInfo.InvariantCulture) <= clock.GetUtcNow())
            throw new BusinessException("expired_confirmation", "Intent expired. Prepare a new intent with a new key.");
    }

    public string BeginAudit(string actor, string tool, string safeArguments)
    {
        using var db = Open();
        var id = Guid.NewGuid().ToString("N");
        using var cmd = Command(db, "INSERT INTO audit(id,timestamp,actor,tool,arguments,outcome) VALUES(@id,@now,@actor,@tool,@args,'started')", null,
            ("@id", id), ("@now", Stamp(clock.GetUtcNow())), ("@actor", actor), ("@tool", tool), ("@args", safeArguments));
        cmd.ExecuteNonQuery();
        return id;
    }

    public void FinishAudit(string id, string outcome, string resultCode, long durationMs)
    {
        using var db = Open();
        using var cmd = Command(db, "UPDATE audit SET outcome=@outcome,result_code=@code,duration_ms=@duration WHERE id=@id", null,
            ("@outcome", outcome), ("@code", resultCode), ("@duration", durationMs), ("@id", id));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<AuditEntry> ReadAudit(int limit = 20)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT id,timestamp,actor,tool,arguments,outcome,result_code,duration_ms FROM audit ORDER BY rowid DESC LIMIT @limit", null,
            ("@limit", Math.Clamp(limit, 1, 100)));
        using var r = cmd.ExecuteReader();
        var entries = new List<AuditEntry>();
        while (r.Read()) entries.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetInt64(7)));
        return entries;
    }

    private static void ValidateId(string value, string prefix)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length > 50 ||
            !value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new BusinessException("invalid_argument", "Invalid identifier.");
    }
    private static void ValidateKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 8 or > 100 || value.Any(char.IsControl))
            throw new BusinessException("invalid_argument", "Idempotency key must contain 8–100 characters without control characters.");
    }
    private static BusinessException Conflict() => new("idempotency_conflict", "This idempotency key was already used for different arguments.");
    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
