using System.Diagnostics;
using BenchmarkApp.Bench;
using BenchmarkApp.Config;
using BenchmarkApp.Data;
using BenchmarkApp.Models;
using BenchmarkApp.Queries;
using MongoDB.Driver;
using Neo4j.Driver;

namespace BenchmarkApp.Web;

// Sve /api/* rute na jednom mjestu. Namjerno tanak sloj - poziva ISKLJUCIVO
// Queries/* i Bench/Verifier.cs (za usporedbu), nema vlastite kopije upita
// ni logike izvrsavanja (Faza 2 plana: jedinstven izvor istine).
public static class Endpoints
{
    public static void MapApi(WebApplication app, AppConfig config, string dataRawDir, IDriver neo4jDriver, IMongoDatabase mongoDatabase)
    {
        app.MapGet("/api/subjects", () =>
        {
            List<string> subjects = SubjectsAndPairsReader.ReadSubjects(dataRawDir);
            List<PairRaw> pairs = SubjectsAndPairsReader.ReadPairs(dataRawDir);
            return Results.Ok(new SubjectsResponse(
                subjects,
                pairs.Select(p => new PairDto(p.FromId, p.ToId, p.ExpectedLength)).ToList()));
        });

        app.MapPost("/api/run", async (RunRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query))
            {
                return Results.BadRequest(new { error = "'query' je obavezan (Q1-Q5)." });
            }

            RunResponse response = req.Query == "Q5"
                ? await RunQ5(neo4jDriver, mongoDatabase, config, req)
                : await RunQ1ToQ4(neo4jDriver, mongoDatabase, config, req);

            return Results.Ok(response);
        });

        app.MapPost("/api/bench", async (BenchRequest req) =>
        {
            BenchResponse response = await RunMiniBench(neo4jDriver, mongoDatabase, config, req);
            return Results.Ok(response);
        });

        app.MapPost("/api/raw", async (RawRequest req) =>
        {
            if (req.Engine == "cypher")
            {
                RawResponse cypherResult = await ExecuteRawCypher(neo4jDriver, req.Text);
                return cypherResult.Ok ? Results.Ok(cypherResult) : Results.BadRequest(cypherResult);
            }

            if (req.Engine == "mql")
            {
                RawResponse mqlResult = await RawMongoGuard.ExecuteAsync(mongoDatabase, req.Text);
                return mqlResult.Ok ? Results.Ok(mqlResult) : Results.BadRequest(mqlResult);
            }

            return Results.BadRequest(new RawResponse(false, null, 0, "'engine' mora biti 'cypher' ili 'mql'."));
        });
    }

    // ---- /api/run: Q1-Q4 (jedan subjekt, uspoređuje se skup ID-eva) -------

    private static async Task<RunResponse> RunQ1ToQ4(IDriver neo4j, IMongoDatabase mongo, AppConfig config, RunRequest req)
    {
        string? subject = req.Subject;
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("'subject' je obavezan za Q1-Q4.");
        }

        var variants = new List<RunVariantResult>();
        var idSets = new List<(string Name, IEnumerable<string> Ids)>();

        // Redoslijed IZVRSAVANJA (neo4j, mongo, mongo-raw) isti kao Bench/Runner.cs
        // - "naizmjenicno istim redoslijedom kao Runner" iz Fazi 4 plana.
        var swN = Stopwatch.StartNew();
        (object? rows, int rowCount, List<string> ids) neo4j1 = await ExecuteNeo4j(neo4j, req.Query, subject);
        swN.Stop();
        variants.Add(new RunVariantResult("neo4j", swN.Elapsed.TotalMilliseconds, neo4j1.rowCount, neo4j1.rows,
            QueryCatalog.Neo4jText(req.Query, subject, null, config.Generator.MaxHops), null));
        idSets.Add(("neo4j", neo4j1.ids));

        var swM = Stopwatch.StartNew();
        (object? rows, int rowCount, List<string> ids) mongo1 = await ExecuteMongo(mongo, req.Query, subject);
        swM.Stop();
        variants.Add(new RunVariantResult("mongo", swM.Elapsed.TotalMilliseconds, mongo1.rowCount, mongo1.rows,
            QueryCatalog.MongoProgrammaticText(req.Query), null));
        idSets.Add(("mongo", mongo1.ids));

        var swR = Stopwatch.StartNew();
        (object? rows, int rowCount, List<string> ids) raw1 = await ExecuteMongoRaw(mongo, req.Query, subject);
        swR.Stop();
        variants.Add(new RunVariantResult("mongo-raw", swR.Elapsed.TotalMilliseconds, raw1.rowCount, raw1.rows,
            QueryCatalog.MongoRawText(req.Query, subject, null, config.Generator.MaxHops), null));
        idSets.Add(("mongo-raw", raw1.ids));

        bool allMatch = Verifier.CompareIdSets(req.Query, subject, idSets.ToArray());
        return new RunResponse(variants, allMatch);
    }

    private static async Task<(object? Rows, int RowCount, List<string> Ids)> ExecuteNeo4j(IDriver neo4j, string queryId, string subject)
    {
        switch (queryId)
        {
            case "Q1":
                UserProfile? p = await Neo4jQueries.Q1(neo4j, subject);
                return (p, p is null ? 0 : 1, p is null ? new List<string>() : new List<string> { p.Id });
            case "Q2":
                List<UserSummary> q2 = await Neo4jQueries.Q2(neo4j, subject);
                return (q2, q2.Count, q2.Select(u => u.Id).ToList());
            case "Q3":
                List<UserSummary> q3 = await Neo4jQueries.Q3(neo4j, subject);
                return (q3, q3.Count, q3.Select(u => u.Id).ToList());
            case "Q4":
                List<FeedItem> q4 = await Neo4jQueries.Q4(neo4j, subject);
                return (q4, q4.Count, q4.Select(f => f.PostId).ToList());
            default:
                throw new ArgumentException($"Nepoznat upit: {queryId}");
        }
    }

    private static async Task<(object? Rows, int RowCount, List<string> Ids)> ExecuteMongo(IMongoDatabase mongo, string queryId, string subject)
    {
        switch (queryId)
        {
            case "Q1":
                UserProfile? p = await MongoQueries.Q1(mongo, subject);
                return (p, p is null ? 0 : 1, p is null ? new List<string>() : new List<string> { p.Id });
            case "Q2":
                List<UserSummary> q2 = await MongoQueries.Q2(mongo, subject);
                return (q2, q2.Count, q2.Select(u => u.Id).ToList());
            case "Q3":
                List<UserSummary> q3 = await MongoQueries.Q3(mongo, subject);
                return (q3, q3.Count, q3.Select(u => u.Id).ToList());
            case "Q4":
                List<FeedItem> q4 = await MongoQueries.Q4(mongo, subject);
                return (q4, q4.Count, q4.Select(f => f.PostId).ToList());
            default:
                throw new ArgumentException($"Nepoznat upit: {queryId}");
        }
    }

    private static async Task<(object? Rows, int RowCount, List<string> Ids)> ExecuteMongoRaw(IMongoDatabase mongo, string queryId, string subject)
    {
        switch (queryId)
        {
            case "Q1":
                UserProfile? p = await MongoRawQueries.Q1(mongo, subject);
                return (p, p is null ? 0 : 1, p is null ? new List<string>() : new List<string> { p.Id });
            case "Q2":
                List<UserSummary> q2 = await MongoRawQueries.Q2(mongo, subject);
                return (q2, q2.Count, q2.Select(u => u.Id).ToList());
            case "Q3":
                List<UserSummary> q3 = await MongoRawQueries.Q3(mongo, subject);
                return (q3, q3.Count, q3.Select(u => u.Id).ToList());
            case "Q4":
                List<FeedItem> q4 = await MongoRawQueries.Q4(mongo, subject);
                return (q4, q4.Count, q4.Select(f => f.PostId).ToList());
            default:
                throw new ArgumentException($"Nepoznat upit: {queryId}");
        }
    }

    // ---- /api/run: Q5 (par from->to, uspoređuje se SAMO duljina) ----------

    private static async Task<RunResponse> RunQ5(IDriver neo4j, IMongoDatabase mongo, AppConfig config, RunRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.FromId) || string.IsNullOrWhiteSpace(req.ToId))
        {
            throw new ArgumentException("'fromId' i 'toId' su obavezni za Q5.");
        }
        string fromId = req.FromId;
        string toId = req.ToId;
        int maxHops = config.Generator.MaxHops;

        var swN = Stopwatch.StartNew();
        PathResult neo4jResult = await Neo4jQueries.Q5(neo4j, fromId, toId, maxHops);
        swN.Stop();
        var swM = Stopwatch.StartNew();
        PathResult mongoResult = await MongoQueries.Q5(mongo, fromId, toId, maxHops);
        swM.Stop();
        var swR = Stopwatch.StartNew();
        PathResult rawResult = await MongoRawQueries.Q5(mongo, fromId, toId, maxHops);
        swR.Stop();

        var variants = new List<RunVariantResult>
        {
            new("neo4j", swN.Elapsed.TotalMilliseconds, neo4jResult.Length, neo4jResult, QueryCatalog.Neo4jText("Q5", fromId, toId, maxHops), null),
            new("mongo", swM.Elapsed.TotalMilliseconds, mongoResult.Length, mongoResult, QueryCatalog.MongoProgrammaticText("Q5"), null),
            new("mongo-raw", swR.Elapsed.TotalMilliseconds, rawResult.Length, rawResult, QueryCatalog.MongoRawText("Q5", fromId, toId, maxHops),
                // v. MongoRawQueries.Q5 XML komentar i NOTES.md - $graphLookup ne vraca sam put.
                "Put nije dostupan - $graphLookup vraca samo dubinu, ne i lanac cvorova."),
        };

        bool allMatch = Verifier.CompareLength("Q5", $"{fromId}->{toId}",
            ("neo4j", neo4jResult.Length), ("mongo", mongoResult.Length), ("mongo-raw", rawResult.Length));

        return new RunResponse(variants, allMatch);
    }

    // ---- /api/bench: mini-benchmark na zahtjev ----------------------------

    // NAPOMENA (Faza 5 plana): mjerenje IZ SPREMNIKA nije mjerodavno - app
    // spremnik se natjece za CPU s neo4j/mongo spremnicima, pa brojke ovdje
    // NISU usporedive s "dotnet run -- bench" na hostu. "Warning" tekst u
    // odgovoru UI ispisuje na ekranu (v. wwwroot/index.html) - to je zahtjev
    // iz plana, ne kozmetika.
    private const string BenchWarning =
        "Mjerodavni brojevi su u results/summary.md (dotnet run -- bench na hostu). " +
        "Ovaj mini-benchmark se izvrsava IZ web spremnika, koji se natjece za CPU s " +
        "neo4j/mongo spremnicima - brojke ovdje nisu usporedive s onima na hostu.";

    private static async Task<BenchResponse> RunMiniBench(IDriver neo4j, IMongoDatabase mongo, AppConfig config, BenchRequest req)
    {
        int iterations = Math.Clamp(req.Iterations <= 0 ? 10 : req.Iterations, 1, 100);
        const int warmup = 2;

        var elapsedByVariant = new Dictionary<string, List<double>> { ["neo4j"] = new(), ["mongo"] = new(), ["mongo-raw"] = new() };

        for (int i = 0; i < warmup + iterations; i++)
        {
            double neo4jMs = await TimeQuery(() => ExecuteNeo4jOrQ5(neo4j, mongo, config, req, "neo4j"));
            double mongoMs = await TimeQuery(() => ExecuteNeo4jOrQ5(neo4j, mongo, config, req, "mongo"));
            double rawMs = await TimeQuery(() => ExecuteNeo4jOrQ5(neo4j, mongo, config, req, "mongo-raw"));

            if (i < warmup)
            {
                continue;
            }

            elapsedByVariant["neo4j"].Add(neo4jMs);
            elapsedByVariant["mongo"].Add(mongoMs);
            elapsedByVariant["mongo-raw"].Add(rawMs);
        }

        var variants = elapsedByVariant.Select(kv => new BenchVariantResult(
            kv.Key,
            Stats.Median(kv.Value),
            Stats.Min(kv.Value),
            Stats.P95(kv.Value),
            Stats.Max(kv.Value))).ToList();

        return new BenchResponse(variants, BenchWarning);
    }

    private static async Task<double> TimeQuery(Func<Task> call)
    {
        var sw = Stopwatch.StartNew();
        await call();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static async Task ExecuteNeo4jOrQ5(IDriver neo4j, IMongoDatabase mongo, AppConfig config, BenchRequest req, string variant)
    {
        if (req.Query == "Q5")
        {
            string fromId = req.FromId ?? throw new ArgumentException("'fromId' je obavezan za Q5.");
            string toId = req.ToId ?? throw new ArgumentException("'toId' je obavezan za Q5.");
            int maxHops = config.Generator.MaxHops;

            _ = variant switch
            {
                "neo4j" => await Neo4jQueries.Q5(neo4j, fromId, toId, maxHops),
                "mongo" => await MongoQueries.Q5(mongo, fromId, toId, maxHops),
                _ => await MongoRawQueries.Q5(mongo, fromId, toId, maxHops),
            };
            return;
        }

        string subject = req.Subject ?? throw new ArgumentException("'subject' je obavezan za Q1-Q4.");
        _ = variant switch
        {
            "neo4j" => await ExecuteNeo4j(neo4j, req.Query, subject),
            "mongo" => await ExecuteMongo(mongo, req.Query, subject),
            _ => await ExecuteMongoRaw(mongo, req.Query, subject),
        };
    }

    // ---- /api/raw: slobodan unos (Faza 5 plana - MORA biti read-only) -----

    private static async Task<RawResponse> ExecuteRawCypher(IDriver neo4j, string text)
    {
        await using var session = neo4j.AsyncSession();
        try
        {
            // ExecuteReadAsync (NE ExecuteWriteAsync) je jamstvo koje provodi
            // SAM Neo4j - upisna naredba (CREATE/DELETE/SET/MERGE/...) unutar
            // ovoga baca iznimku prije nego dotakne podatke. To je stvarna
            // obrana, ne string-provjera kao (dodatna) prva linija.
            List<Dictionary<string, object?>> rows = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(text);
                var records = await cursor.ToListAsync();
                return records.Take(500).Select(RecordToDict).ToList();
            });

            return new RawResponse(true, rows, rows.Count, null);
        }
        catch (Exception ex)
        {
            // Ako je razlog odbijanja upisna naredba u read transakciji,
            // Neo4j.Driver baca iznimku s jasnom porukom ("Writing in read
            // access mode not allowed") - ta poruka se ovdje samo prosljeduje,
            // ne prepisuje.
            return new RawResponse(false, null, 0, ex.Message);
        }
    }

    private static Dictionary<string, object?> RecordToDict(IRecord record)
    {
        var result = new Dictionary<string, object?>();
        foreach (string key in record.Keys)
        {
            object? value = record[key];
            result[key] = value is Neo4j.Driver.ZonedDateTime zdt ? zdt.ToDateTimeOffset() : value;
        }
        return result;
    }
}
