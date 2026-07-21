// Candidate B spike: native pg_catalog comparison with shadow-database
// normalization. Throwaway code — the observations it prints are the artifact.
using Npgsql;

const string Schema = "vibebase_control";

string fixtures = FindFixtures();
string v1 = File.ReadAllText(Path.Combine(fixtures, "v1", "vibebase_control.sql"));
string v2 = File.ReadAllText(Path.Combine(fixtures, "v2", "vibebase_control.sql"));

// [1] target에 v1 apply — 도구 소유 단일 트랜잭션
await Apply("spike_target", v1);
Console.WriteLine("[1] v1 applied to spike_target in a tool-owned transaction");

// [2] 재-diff: desired(v1)를 shadow에 정규화 apply 후 target과 비교 → empty 기대
Console.WriteLine($"[2] re-diff v1: {await Diff(v1)} differences (expect 0)");

// [3] 원자성 실증: v2 델타 + 고의 실패를 한 트랜잭션으로 → 롤백 → Tier 부재 확인
try
{
    await Apply("spike_target", AlterToV2() + "\nDO $$ BEGIN RAISE EXCEPTION 'injected failure'; END $$;");
}
catch (PostgresException e)
{
    Console.WriteLine($"[3] injected failure: {e.SqlState} — checking rollback…");
}
Console.WriteLine($"[3] Tier column exists after failed apply: {await ColumnExists("spike_target", "Workspaces", "Tier")} (expect False)");

// [4] v2 델타 정상 apply → 재-diff empty 기대
await Apply("spike_target", AlterToV2());
Console.WriteLine($"[4] re-diff v2: {await Diff(v2)} differences (expect 0)");

// [5] R3 실측: CHECK 정의의 canonical form 원문 — desired 텍스트 비교가 왜 실패하는지
await DumpConstraints("spike_target");

// [6] shadow가 스크래치 DB가 아니라 동일-DB 스크래치 스키마여도 성립하는가 (관리형 PG에서
//     CREATE DATABASE 권한이 없을 수 있음) — 스키마명 치환 apply 후 동일 비교
Console.WriteLine($"[6] same-DB scratch-schema diff v2: {await DiffInScratchSchema(v2)} differences (expect 0)");

static string AlterToV2() => """
    ALTER TABLE vibebase_control."Workspaces" ADD COLUMN "Tier" text NOT NULL DEFAULT 'free';
    ALTER TABLE vibebase_control."Workspaces" ADD CONSTRAINT "CK_Workspaces_Tier" CHECK ("Tier" IN ('free', 'pro'));
    ALTER TABLE vibebase_control."Resources" ADD CONSTRAINT "UQ_Resources_App_ExternalRef" UNIQUE ("AppId", "ExternalRef");
    """;

static string FindFixtures()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "fixtures")))
        dir = dir.Parent;
    return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("fixtures not found"), "fixtures");
}

static NpgsqlConnection Open(string db)
{
    var c = new NpgsqlConnection($"Host=localhost;Port=15544;Database={db};Username=spike_app;Password=spike_app");
    c.Open();
    return c;
}

// 도구가 트랜잭션 경계를 소유한다 — R5 축의 실증. 원장 기록·planHash 재검증을
// 이 트랜잭션 안에 함께 넣을 수 있다는 것이 서브프로세스 후보와의 차이다.
static async Task Apply(string db, string sql)
{
    await using var con = Open(db);
    await using var tx = await con.BeginTransactionAsync();
    await using var cmd = new NpgsqlCommand(sql, con, tx);
    await cmd.ExecuteNonQueryAsync();
    await tx.CommitAsync();
}

// diff = shadow(desired 재생성)와 target의 introspection 스냅숏 집합 비교.
// shadow apply가 곧 정규화다: 양쪽 다 엔진이 재출력한 canonical form으로 비교된다.
static async Task<int> Diff(string desiredSql)
{
    await using (var con = Open("spike_shadow"))
    {
        await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {Schema} CASCADE;", con);
        await drop.ExecuteNonQueryAsync();
    }
    await Apply("spike_shadow", desiredSql);
    var target = await Snapshot("spike_target", Schema);
    var shadow = await Snapshot("spike_shadow", Schema);
    var diff = target.Except(shadow).Concat(shadow.Except(target)).ToList();
    foreach (var d in diff) Console.WriteLine($"    diff: {d}");
    return diff.Count;
}

// [6]용: desired의 스키마명을 스크래치 스키마로 치환해 target DB 안에 재생성 후 비교.
// 치환은 스파이크 수준의 단순 문자열 치환 — 본 구현이라면 파서 기반이어야 한다.
static async Task<int> DiffInScratchSchema(string desiredSql)
{
    const string Scratch = "schemorph_scratch";
    string rewritten = desiredSql.Replace(Schema, Scratch);
    await using (var con = Open("spike_target"))
    {
        await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {Scratch} CASCADE;", con);
        await drop.ExecuteNonQueryAsync();
    }
    await Apply("spike_target", rewritten);
    var live = await Snapshot("spike_target", Schema);
    var scratch = (await Snapshot("spike_target", Scratch))
        .Select(r => r.Replace(Scratch, Schema)).ToHashSet();
    var diff = live.Except(scratch).Concat(scratch.Except(live)).ToList();
    foreach (var d in diff) Console.WriteLine($"    diff: {d}");
    await using (var con = Open("spike_target"))
    {
        await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {Scratch} CASCADE;", con);
        await drop.ExecuteNonQueryAsync();
    }
    return diff.Count;
}

// canonical 스냅숏: 컬럼(순서 무시 — Schemorph는 상태를 diff하지 서수를 하지 않는다) + 제약 + 인덱스
static async Task<HashSet<string>> Snapshot(string db, string schema)
{
    var rows = new HashSet<string>();
    await using var con = Open(db);
    await Collect(con, rows, "col", $"""
        SELECT c.relname, a.attname,
               format_type(a.atttypid, a.atttypmod), a.attnotnull,
               COALESCE(pg_get_expr(d.adbin, d.adrelid), '')
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        WHERE n.nspname = '{schema}' AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped
        """);
    await Collect(con, rows, "con", $"""
        SELECT c.relname, con.conname, pg_get_constraintdef(con.oid)
        FROM pg_constraint con
        JOIN pg_class c ON c.oid = con.conrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = '{schema}'
        """);
    await Collect(con, rows, "idx", $"""
        SELECT tablename, indexname, indexdef
        FROM pg_indexes WHERE schemaname = '{schema}'
        """);
    return rows;
}

static async Task Collect(NpgsqlConnection con, HashSet<string> rows, string tag, string sql)
{
    await using var cmd = new NpgsqlCommand(sql, con);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        rows.Add(tag + "|" + string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.GetValue(i)?.ToString())));
}

static async Task<bool> ColumnExists(string db, string table, string column)
{
    await using var con = Open(db);
    await using var cmd = new NpgsqlCommand(
        $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='{Schema}' AND table_name='{table}' AND column_name='{column}'", con);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
}

static async Task DumpConstraints(string db)
{
    await using var con = Open(db);
    await using var cmd = new NpgsqlCommand($"""
        SELECT con.conname, pg_get_constraintdef(con.oid)
        FROM pg_constraint con
        JOIN pg_class c ON c.oid = con.conrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = '{Schema}' AND con.contype = 'c'
        ORDER BY con.conname
        """, con);
    await using var r = await cmd.ExecuteReaderAsync();
    Console.WriteLine("[5] CHECK canonical forms (desired text was `\"Status\" IN ('active', 'suspended')` 등):");
    while (await r.ReadAsync())
        Console.WriteLine($"    {r.GetString(0)}: {r.GetString(1)}");
}
