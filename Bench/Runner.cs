using System.Diagnostics;
using System.Globalization;
using BenchmarkApp.Config;
using BenchmarkApp.Data;
using BenchmarkApp.Models;
using BenchmarkApp.Queries;
using MongoDB.Driver;
using Neo4j.Driver;

namespace BenchmarkApp.Bench;

// "dotnet run -- bench" - za svaki upit i svaki subjekt/par, izmjeri
// warmup(5) + measurements(20) INTERLEAVED pozive - interleaving znaci da
// SVE TRI varijante (neo4j, mongo, mongo-raw) "dijele" trenutne uvjete
// stroja (CPU, I/O) unutar SVAKE iteracije, umjesto da npr. SVA Neo4j
// mjerenja idu prva - to bi sistematski favoriziralo varijantu koja je
// mjerena kad je stroj bio "hladniji"/manje opterecen. Fiksni redoslijed
// unutar iteracije: neo4j, mongo, mongo-raw (v. RunInterleaved niže).
public static class Runner
{
    // Redoslijed varijanti - koristi se za sve tri ispisne tablice i za
    // RunInterleaved, na jednom mjestu da ostane dosljedan posvuda.
    private static readonly string[] Variants = { "neo4j", "mongo", "mongo-raw" };

    public static async Task<int> RunAsync(AppConfig config, string dataRawDir, string resultsDir)
    {
        // Plan zabranjuje mjerenje dok verify ne prode cist - ovo NIJE
        // podsjetnik korisniku da to sam pokrene rucno, nego automatska
        // provjera: bench sam prvo pokrene verify i odustane ako nesto padne.
        Console.WriteLine("Bench: provjeravam da su baze semanticki uskladene prije mjerenja...");
        int verifyExitCode = await VerifyRunner.RunAsync(config, dataRawDir);
        if (verifyExitCode != 0)
        {
            Console.WriteLine("Bench: PREKINUTO - verify nije proslo cisto, mjerenje se ne pokrece.");
            return verifyExitCode;
        }
        Console.WriteLine();

        List<string> subjectIds = SubjectsAndPairsReader.ReadSubjects(dataRawDir);
        List<PairRaw> pairs = SubjectsAndPairsReader.ReadPairs(dataRawDir);

        int warmup = config.Bench.Warmup;
        int measurements = config.Bench.Measurements;

        using var neo4jDriver = GraphDatabase.Driver(
            config.Neo4j.Uri,
            AuthTokens.Basic(config.Neo4j.User, config.Neo4j.Password));

        var mongoClient = new MongoClient(config.Mongo.ConnectionString);
        IMongoDatabase mongoDatabase = mongoClient.GetDatabase(config.Mongo.Database);

        var rows = new List<TimingRow>();

        Console.WriteLine($"Bench: mjerim Q1-Q4 za {subjectIds.Count} subjekata (warmup={warmup}, measurements={measurements})...");
        foreach (string subjectId in subjectIds)
        {
            await MeasureQ1(neo4jDriver, mongoDatabase, subjectId, warmup, measurements, rows);
            await MeasureQ2(neo4jDriver, mongoDatabase, subjectId, warmup, measurements, rows);
            await MeasureQ3(neo4jDriver, mongoDatabase, subjectId, warmup, measurements, rows);
            await MeasureQ4(neo4jDriver, mongoDatabase, subjectId, warmup, measurements, rows);
        }

        Console.WriteLine($"Bench: mjerim Q5 za {pairs.Count} parova...");
        foreach (PairRaw pair in pairs)
        {
            await MeasureQ5(neo4jDriver, mongoDatabase, pair, config.Generator.MaxHops, warmup, measurements, rows);
        }

        Console.WriteLine("Bench: pisem results/timings.csv...");
        TimingsWriter.Write(resultsDir, rows);

        PrintTable1SpeedByQuery(rows);
        PrintTable2RangeByQueryAndDb(rows);
        PrintTable3LoadAndDisk(resultsDir);

        WriteSummaryMarkdown(resultsDir, rows);

        return 0;
    }

    // ---- Mjerenje po upitu -------------------------------------------

    // Q1 - rows_returned je 0 (nije nadeno) ili 1 (nadeno) - dosljedno s
    // konvencijom da "rows_returned" znaci "koliko je stvari vraceno".
    private static async Task MeasureQ1(
        IDriver neo4j, IMongoDatabase mongo, string subjectId,
        int warmup, int measurements, List<TimingRow> rows)
    {
        await RunInterleaved(
            "Q1", subjectId, warmup, measurements, rows,
            Timed(async () =>
            {
                UserProfile? result = await Neo4jQueries.Q1(neo4j, subjectId);
                return result is null ? 0 : 1;
            }),
            Timed(async () =>
            {
                UserProfile? result = await MongoQueries.Q1(mongo, subjectId);
                return result is null ? 0 : 1;
            }),
            Timed(async () =>
            {
                UserProfile? result = await MongoRawQueries.Q1(mongo, subjectId);
                return result is null ? 0 : 1;
            }));
    }

    private static async Task MeasureQ2(
        IDriver neo4j, IMongoDatabase mongo, string subjectId,
        int warmup, int measurements, List<TimingRow> rows)
    {
        await RunInterleaved(
            "Q2", subjectId, warmup, measurements, rows,
            Timed(async () => (await Neo4jQueries.Q2(neo4j, subjectId)).Count),
            Timed(async () => (await MongoQueries.Q2(mongo, subjectId)).Count),
            Timed(async () => (await MongoRawQueries.Q2(mongo, subjectId)).Count));
    }

    private static async Task MeasureQ3(
        IDriver neo4j, IMongoDatabase mongo, string subjectId,
        int warmup, int measurements, List<TimingRow> rows)
    {
        await RunInterleaved(
            "Q3", subjectId, warmup, measurements, rows,
            Timed(async () => (await Neo4jQueries.Q3(neo4j, subjectId)).Count),
            Timed(async () => (await MongoQueries.Q3(mongo, subjectId)).Count),
            Timed(async () => (await MongoRawQueries.Q3(mongo, subjectId)).Count));
    }

    private static async Task MeasureQ4(
        IDriver neo4j, IMongoDatabase mongo, string subjectId,
        int warmup, int measurements, List<TimingRow> rows)
    {
        await RunInterleaved(
            "Q4", subjectId, warmup, measurements, rows,
            Timed(async () => (await Neo4jQueries.Q4(neo4j, subjectId)).Count),
            Timed(async () => (await MongoQueries.Q4(mongo, subjectId)).Count),
            Timed(async () => (await MongoRawQueries.Q4(mongo, subjectId)).Count));
    }

    // Q5 - rows_returned = duljina puta (broj hopova), NE broj cvorova -
    // to je "stavka" koja ima smisla usporedivati kroz mjerenja ovog upita
    // (v. Models/TimingRow.cs).
    private static async Task MeasureQ5(
        IDriver neo4j, IMongoDatabase mongo, PairRaw pair, int maxHops,
        int warmup, int measurements, List<TimingRow> rows)
    {
        string pairLabel = $"{pair.FromId}->{pair.ToId}";

        await RunInterleaved(
            "Q5", pairLabel, warmup, measurements, rows,
            Timed(async () => (await Neo4jQueries.Q5(neo4j, pair.FromId, pair.ToId, maxHops)).Length),
            Timed(async () => (await MongoQueries.Q5(mongo, pair.FromId, pair.ToId, maxHops)).Length),
            Timed(async () => (await MongoRawQueries.Q5(mongo, pair.FromId, pair.ToId, maxHops)).Length));
    }

    // Omata poziv upita stopericom - dijeljena pomocna funkcija da se
    // Stopwatch logika ne ponavlja 15 puta (5 upita x 3 varijante).
    private static Func<Task<(double ElapsedMs, int RowsReturned)>> Timed(Func<Task<int>> call)
    {
        return async () =>
        {
            var sw = Stopwatch.StartNew();
            int rowsReturned = await call();
            sw.Stop();
            return (sw.Elapsed.TotalMilliseconds, rowsReturned);
        };
    }

    // Zajednicka interleaved warmup+measurement petlja - poziva varijante
    // REDOM (neo4j, mongo, mongo-raw - v. "Variants" polje) UNUTAR SVAKE
    // iteracije, prvih "warmup" iteracija se odbacuje (samo zagrijava
    // JIT/cache/connection pool), preostalih "measurements" iteracija se
    // upisuje u "rows".
    private static async Task RunInterleaved(
        string query,
        string subjectLabel,
        int warmup,
        int measurements,
        List<TimingRow> rows,
        params Func<Task<(double ElapsedMs, int RowsReturned)>>[] calls)
    {
        int totalIterations = warmup + measurements;

        for (int i = 0; i < totalIterations; i++)
        {
            var results = new (double ElapsedMs, int RowsReturned)[calls.Length];
            for (int v = 0; v < calls.Length; v++)
            {
                results[v] = await calls[v]();
            }

            if (i < warmup)
            {
                continue; // warmup - namjerno se ne biljezi
            }

            int runIndex = i - warmup;
            for (int v = 0; v < calls.Length; v++)
            {
                rows.Add(new TimingRow(query, Variants[v], subjectLabel, runIndex, results[v].ElapsedMs, results[v].RowsReturned));
            }
        }
    }

    // ---- Konzolne tablice ----------------------------------------------

    private static void PrintTable1SpeedByQuery(List<TimingRow> rows)
    {
        Console.WriteLine();
        Console.WriteLine("=== Tablica 1: brzina po upitu (medijan / p95) ===");
        Console.WriteLine(
            $"{"Upit",-5} | {"neo4j (ms)",17} | {"mongo (ms)",17} | {"mongo-raw (ms)",17} | Omjer (najbrza varijanta)");

        foreach (string query in new[] { "Q1", "Q2", "Q3", "Q4", "Q5" })
        {
            Dictionary<string, List<double>> byVariant = Variants.ToDictionary(v => v, v => ElapsedMsFor(rows, query, v));
            Dictionary<string, double> medians = byVariant.ToDictionary(kv => kv.Key, kv => Stats.Median(kv.Value));
            Dictionary<string, double> p95s = byVariant.ToDictionary(kv => kv.Key, kv => Stats.P95(kv.Value));

            string cells = string.Join(" | ", Variants.Select(v =>
                $"{FormatMs(medians[v]),8} / p95 {FormatMs(p95s[v]),8}"));
            string ratio = FormatRatio(medians);

            Console.WriteLine($"{query,-5} | {cells} | {ratio}");
        }
    }

    private static void PrintTable2RangeByQueryAndDb(List<TimingRow> rows)
    {
        Console.WriteLine();
        Console.WriteLine("=== Tablica 2: raspon mjerenja po upitu i varijanti ===");
        Console.WriteLine($"{"Upit",-5} | {"Varijanta",-9} | {"Min (ms)",9} | {"p95 (ms)",9} | {"Max (ms)",9} | Prosj. broj vracenih redaka");

        foreach (string query in new[] { "Q1", "Q2", "Q3", "Q4", "Q5" })
        {
            foreach (string variant in Variants)
            {
                List<TimingRow> subset = rows.Where(r => r.Query == query && r.Db == variant).ToList();
                if (subset.Count == 0)
                {
                    continue;
                }

                List<double> elapsed = subset.Select(r => r.ElapsedMs).ToList();
                double min = Stats.Min(elapsed);
                double p95 = Stats.P95(elapsed);
                double max = Stats.Max(elapsed);
                double avgRows = Stats.Average(subset.Select(r => r.RowsReturned).ToList());

                Console.WriteLine(
                    $"{query,-5} | {variant,-9} | {min,9:F2} | {p95,9:F2} | {max,9:F2} | {avgRows,20:F1}");
            }
        }
    }

    private static void PrintTable3LoadAndDisk(string resultsDir)
    {
        Console.WriteLine();
        Console.WriteLine("=== Tablica 3: ucitavanje i zauzece na disku ===");

        List<string[]> lastRowPerDb = ReadLastDbStatsRowPerDb(resultsDir);
        if (lastRowPerDb.Count == 0)
        {
            Console.WriteLine("(results/db_stats.csv ne postoji - pokreni load-neo4j i load-mongo prije bench-a.)");
            return;
        }

        Console.WriteLine($"{"Baza",-6} | {"Vrijeme ucitavanja (s)",22} | Zauzece na disku (MB)");
        foreach (string[] row in lastRowPerDb)
        {
            // Stupci: db,load_time_sec,disk_mb
            Console.WriteLine($"{row[0],-6} | {row[1],22} | {row[2]}");
        }
    }

    // db_stats.csv se DOPISUJE (append) svaki put kad se load-neo4j/
    // load-mongo pokrenu (v. Load/DbStatsWriter.cs - namjerno, jer su to
    // dvije odvojene naredbe koje ne smiju brisati redak one druge). Ako je
    // netko tijekom razvoja pokrenuo loadere vise puta (npr. testirajuci
    // idempotentnost), datoteka ce imati VISE redaka za istu bazu - za
    // izvjestavanje je smislen samo POSLJEDNJI (najnoviji) redak po bazi,
    // jer on odgovara podacima koji su STVARNO u bazi dok se bench pokrece.
    //
    // "mongo-raw" NEMA svoj load - dijeli Mongovu bazu (isti podaci, samo
    // drukciji upiti nad njima), zato poredak ovdje OSTAJE "neo4j", "mongo"
    // (ne dodaje se treci redak).
    private static List<string[]> ReadLastDbStatsRowPerDb(string resultsDir)
    {
        string path = Path.Combine(resultsDir, "db_stats.csv");
        if (!File.Exists(path))
        {
            return new List<string[]>();
        }

        var lastRowByDb = new Dictionary<string, string[]>();
        foreach (string[] row in CsvReader.ReadRows(path))
        {
            lastRowByDb[row[0]] = row; // kasniji red za istu bazu prepisuje raniji
        }

        // Stabilan ispisni poredak (neo4j pa mongo), neovisan o redoslijedu u datoteci.
        return new[] { "neo4j", "mongo" }
            .Where(lastRowByDb.ContainsKey)
            .Select(db => lastRowByDb[db])
            .ToList();
    }

    // ---- summary.md ------------------------------------------------------

    private static void WriteSummaryMarkdown(string resultsDir, List<TimingRow> rows)
    {
        string path = Path.Combine(resultsDir, "summary.md");
        using var writer = new StreamWriter(path, append: false);

        writer.WriteLine("# Rezultati benchmarka");
        writer.WriteLine();
        writer.WriteLine("## Tablica 1: brzina po upitu (medijan / p95)");
        writer.WriteLine();
        writer.WriteLine("| Upit | neo4j medijan (ms) | neo4j p95 (ms) | mongo medijan (ms) | mongo p95 (ms) | mongo-raw medijan (ms) | mongo-raw p95 (ms) | Omjer (najbrza varijanta) |");
        writer.WriteLine("|---|---|---|---|---|---|---|---|");
        foreach (string query in new[] { "Q1", "Q2", "Q3", "Q4", "Q5" })
        {
            Dictionary<string, List<double>> byVariant = Variants.ToDictionary(v => v, v => ElapsedMsFor(rows, query, v));
            Dictionary<string, double> medians = byVariant.ToDictionary(kv => kv.Key, kv => Stats.Median(kv.Value));
            Dictionary<string, double> p95s = byVariant.ToDictionary(kv => kv.Key, kv => Stats.P95(kv.Value));
            string ratio = FormatRatio(medians);

            writer.WriteLine(
                $"| {query} | {FormatMs(medians["neo4j"])} | {FormatMs(p95s["neo4j"])} | " +
                $"{FormatMs(medians["mongo"])} | {FormatMs(p95s["mongo"])} | " +
                $"{FormatMs(medians["mongo-raw"])} | {FormatMs(p95s["mongo-raw"])} | {ratio} |");
        }

        writer.WriteLine();
        writer.WriteLine("## Tablica 2: raspon mjerenja po upitu i varijanti");
        writer.WriteLine();
        writer.WriteLine("| Upit | Varijanta | Min (ms) | p95 (ms) | Max (ms) | Prosj. broj vracenih redaka |");
        writer.WriteLine("|---|---|---|---|---|---|");
        foreach (string query in new[] { "Q1", "Q2", "Q3", "Q4", "Q5" })
        {
            foreach (string variant in Variants)
            {
                List<TimingRow> subset = rows.Where(r => r.Query == query && r.Db == variant).ToList();
                if (subset.Count == 0)
                {
                    continue;
                }

                List<double> elapsed = subset.Select(r => r.ElapsedMs).ToList();
                double min = Stats.Min(elapsed);
                double p95 = Stats.P95(elapsed);
                double max = Stats.Max(elapsed);
                double avgRows = Stats.Average(subset.Select(r => r.RowsReturned).ToList());

                writer.WriteLine(
                    $"| {query} | {variant} | {min.ToString("F2", CultureInfo.InvariantCulture)} | " +
                    $"{p95.ToString("F2", CultureInfo.InvariantCulture)} | " +
                    $"{max.ToString("F2", CultureInfo.InvariantCulture)} | {avgRows.ToString("F1", CultureInfo.InvariantCulture)} |");
            }
        }

        writer.WriteLine();
        writer.WriteLine("## Tablica 3: ucitavanje i zauzece na disku");
        writer.WriteLine();
        writer.WriteLine("| Baza | Vrijeme ucitavanja (s) | Zauzece na disku (MB) |");
        writer.WriteLine("|---|---|---|");
        foreach (string[] row in ReadLastDbStatsRowPerDb(resultsDir))
        {
            writer.WriteLine($"| {row[0]} | {row[1]} | {row[2]} |");
        }

        Console.WriteLine();
        Console.WriteLine($"Bench: results/summary.md napisan.");
    }

    // ---- pomocne funkcije ------------------------------------------------

    private static List<double> ElapsedMsFor(List<TimingRow> rows, string query, string db)
    {
        return rows.Where(r => r.Query == query && r.Db == db).Select(r => r.ElapsedMs).ToList();
    }

    private static string FormatMs(double ms) => ms.ToString("F2", CultureInfo.InvariantCulture);

    // "neo4j 1.4x" ili "mongo-raw 2.1x" - kaze koja je varijanta najbrza
    // (najnizi medijan) i za koliko puta brza od SLJEDECE najbrze. Ako su
    // svi medijani jednaki (rijetko, ali moguce kod vrlo brzih upita gdje
    // mjerenje zaokruzuje na iste vrijednosti), ispisuje "==".
    private static string FormatRatio(Dictionary<string, double> mediansByVariant)
    {
        var sorted = mediansByVariant.OrderBy(kv => kv.Value).ToList();
        if (sorted[0].Value == sorted[^1].Value)
        {
            return "==";
        }

        double secondFastest = sorted[1].Value;
        double fastest = sorted[0].Value;
        double factor = fastest == 0 ? double.PositiveInfinity : secondFastest / fastest;
        return $"{sorted[0].Key} {factor.ToString("F1", CultureInfo.InvariantCulture)}x";
    }
}
