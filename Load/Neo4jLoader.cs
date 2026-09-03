using System.Diagnostics;
using BenchmarkApp.Config;
using BenchmarkApp.Data;
using BenchmarkApp.Models;
using Neo4j.Driver;

namespace BenchmarkApp.Load;

// Cita data/raw/*.csv i puni Neo4j: (:User) i (:Post) cvorovi,
// [:FOLLOWS] i [:POSTED] veze. Prije punjenja kreira constraint/indekse
// (v. CreateConstraintsAndIndexes) jer bez njih MATCH po id-u pri gradnji
// veza radi pun sken svih cvorova te labele - za 10000+ redaka to bi bilo
// nepotrebno sporo, a mjerimo baš vrijeme punjenja.
public static class Neo4jLoader
{
    // Koliko redaka saljemo u jednom UNWIND upitu. Prevelik broj tro?i
    // previse memorije po transakciji, premalen znaci previse round-tripova
    // - 2000 je razuman kompromis za dataset ove velicine (10k korisnika).
    private const int BatchSize = 2000;

    public static async Task RunAsync(AppConfig config, string dataRawDir, string resultsDir)
    {
        var stopwatch = Stopwatch.StartNew();

        using var driver = GraphDatabase.Driver(
            config.Neo4j.Uri,
            AuthTokens.Basic(config.Neo4j.User, config.Neo4j.Password));

        await using var session = driver.AsyncSession();

        // Loader mora biti ponovljiv - ako se "dotnet run -- load-neo4j"
        // pokrene dvaput, drugi put bi CREATE (zbog user_id_unique/post_id_unique
        // constrainta) bacio gresku na prvom duplikatu. Zato bazu prije
        // punjenja uvijek prvo ispraznimo.
        //
        // "MATCH (n) DETACH DELETE n" u JEDNOJ transakciji puca na ovom
        // datasetu (~50 000 cvorova + ~190 000 veza) uz "dbms.memory.
        // transaction.total.max threshold reached" - jedna transakcija ne
        // smije drzati toliko promjena u memoriji odjednom. "IN TRANSACTIONS
        // OF ... ROWS" dijeli brisanje u manje batcheve, svaki se commita
        // zasebno, pa memorija jedne transakcije ostaje mala.
        Console.WriteLine("Neo4j: brisem postojece podatke...");
        await ExecCypher(session, """
            MATCH (n)
            CALL (n) {
                DETACH DELETE n
            } IN TRANSACTIONS OF 5000 ROWS
            """);

        Console.WriteLine("Neo4j: kreiram constraint/indekse...");
        await CreateConstraintsAndIndexes(session);

        Console.WriteLine("Neo4j: citam CSV-ove...");
        List<UserRaw> users = ReadUsers(dataRawDir);
        List<PostRaw> posts = ReadPosts(dataRawDir);
        List<(string FromId, string ToId)> follows = ReadFollows(dataRawDir);

        Console.WriteLine($"Neo4j: ucitavam {users.Count} korisnika...");
        await LoadUsers(session, users);

        Console.WriteLine($"Neo4j: ucitavam {posts.Count} objava...");
        await LoadPosts(session, posts);

        Console.WriteLine($"Neo4j: ucitavam {follows.Count} FOLLOWS veza...");
        await LoadFollows(session, follows);

        Console.WriteLine($"Neo4j: ucitavam {posts.Count} POSTED veza...");
        await LoadPosted(session, posts);

        stopwatch.Stop();

        // /data je direktorij u kojem Neo4j image drzi bazu (grafovi, indeksi,
        // transaction log) - definiran u sluzbenom neo4j Docker imageu.
        double diskMb = DockerDiskSize.GetDirectorySizeMb("benchmark-neo4j", "/data");
        DbStatsWriter.AppendRow(resultsDir, new DbStats("neo4j", stopwatch.Elapsed.TotalSeconds, diskMb));

        Console.WriteLine(
            $"Neo4j: gotovo za {stopwatch.Elapsed.TotalSeconds:F2}s. " +
            $"Zauzece na disku: {diskMb:F1} MB.");
    }

    private static async Task CreateConstraintsAndIndexes(IAsyncSession session)
    {
        // Neo4j 5 sintaksa (FOR ... REQUIRE) - stara "ON ... ASSERT" sintaksa
        // iz Neo4j 4 na ovom imageu ne radi.
        await ExecCypher(session,
            "CREATE CONSTRAINT user_id_unique IF NOT EXISTS FOR (u:User) REQUIRE u.id IS UNIQUE");
        await ExecCypher(session,
            "CREATE CONSTRAINT post_id_unique IF NOT EXISTS FOR (p:Post) REQUIRE p.id IS UNIQUE");
        await ExecCypher(session,
            "CREATE INDEX post_created_at IF NOT EXISTS FOR (p:Post) ON (p.created_at)");
    }

    private static async Task LoadUsers(IAsyncSession session, List<UserRaw> users)
    {
        const string query = """
            UNWIND $rows AS row
            CREATE (u:User {
                id: row.id,
                username: row.username,
                name: row.name,
                created_at: datetime(row.created_at)
            })
            """;

        foreach (UserRaw[] batch in users.Chunk(BatchSize))
        {
            var rows = batch.Select(u => new Dictionary<string, object>
            {
                ["id"] = u.Id,
                ["username"] = u.Username,
                ["name"] = u.Name,
                ["created_at"] = u.CreatedAt.ToString("o"), // "o" = round-trip ISO 8601, datime() u Cypheru to razumije
            }).ToList();

            await ExecCypher(session, query, new Dictionary<string, object> { ["rows"] = rows });
        }
    }

    private static async Task LoadPosts(IAsyncSession session, List<PostRaw> posts)
    {
        const string query = """
            UNWIND $rows AS row
            CREATE (p:Post {
                id: row.id,
                text: row.text,
                created_at: datetime(row.created_at)
            })
            """;

        foreach (PostRaw[] batch in posts.Chunk(BatchSize))
        {
            var rows = batch.Select(p => new Dictionary<string, object>
            {
                ["id"] = p.Id,
                ["text"] = p.Text,
                ["created_at"] = p.CreatedAt.ToString("o"),
            }).ToList();

            await ExecCypher(session, query, new Dictionary<string, object> { ["rows"] = rows });
        }
    }

    private static async Task LoadFollows(IAsyncSession session, List<(string FromId, string ToId)> follows)
    {
        // MATCH po id-u ovdje koristi user_id_unique constraint (= indeks)
        // umjesto punog skena - zato je constraint kreiran PRIJE ovog koraka.
        const string query = """
            UNWIND $rows AS row
            MATCH (a:User {id: row.from_id})
            MATCH (b:User {id: row.to_id})
            CREATE (a)-[:FOLLOWS]->(b)
            """;

        foreach ((string FromId, string ToId)[] batch in follows.Chunk(BatchSize))
        {
            var rows = batch.Select(f => new Dictionary<string, object>
            {
                ["from_id"] = f.FromId,
                ["to_id"] = f.ToId,
            }).ToList();

            await ExecCypher(session, query, new Dictionary<string, object> { ["rows"] = rows });
        }
    }

    private static async Task LoadPosted(IAsyncSession session, List<PostRaw> posts)
    {
        const string query = """
            UNWIND $rows AS row
            MATCH (u:User {id: row.author_id})
            MATCH (p:Post {id: row.post_id})
            CREATE (u)-[:POSTED]->(p)
            """;

        foreach (PostRaw[] batch in posts.Chunk(BatchSize))
        {
            var rows = batch.Select(p => new Dictionary<string, object>
            {
                ["author_id"] = p.AuthorId,
                ["post_id"] = p.Id,
            }).ToList();

            await ExecCypher(session, query, new Dictionary<string, object> { ["rows"] = rows });
        }
    }

    // Auto-commit upit (bez povratnih redaka) - ConsumeAsync cita cijeli
    // rezultat do kraja, sto garantira da je upis stvarno zavrsio prije
    // nego krenemo na sljedeci batch.
    private static async Task ExecCypher(
        IAsyncSession session,
        string query,
        Dictionary<string, object>? parameters = null)
    {
        IResultCursor cursor = parameters is null
            ? await session.RunAsync(query)
            : await session.RunAsync(query, parameters);

        await cursor.ConsumeAsync();
    }

    private static List<UserRaw> ReadUsers(string dataRawDir)
    {
        string path = Path.Combine(dataRawDir, "users.csv");
        var result = new List<UserRaw>();

        foreach (string[] row in CsvReader.ReadRows(path))
        {
            // Stupci: Id,Username,Name,CreatedAt
            result.Add(new UserRaw(row[0], row[1], row[2], CsvReader.ParseDate(row[3])));
        }

        return result;
    }

    private static List<PostRaw> ReadPosts(string dataRawDir)
    {
        string path = Path.Combine(dataRawDir, "posts.csv");
        var result = new List<PostRaw>();

        foreach (string[] row in CsvReader.ReadRows(path))
        {
            // Stupci: Id,AuthorId,Text,CreatedAt
            result.Add(new PostRaw(row[0], row[1], row[2], CsvReader.ParseDate(row[3])));
        }

        return result;
    }

    private static List<(string FromId, string ToId)> ReadFollows(string dataRawDir)
    {
        string path = Path.Combine(dataRawDir, "follows.csv");
        var result = new List<(string, string)>();

        foreach (string[] row in CsvReader.ReadRows(path))
        {
            // Stupci: FromId,ToId
            result.Add((row[0], row[1]));
        }

        return result;
    }
}
