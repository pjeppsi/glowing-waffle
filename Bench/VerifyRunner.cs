using BenchmarkApp.Config;
using BenchmarkApp.Data;
using BenchmarkApp.Models;
using BenchmarkApp.Queries;
using MongoDB.Driver;
using Neo4j.Driver;

namespace BenchmarkApp.Bench;

// "dotnet run -- verify" - za svih 10 subjekata iz subjects.csv pokrece
// Q1-Q4 na SVE TRI varijante (neo4j, mongo, mongo-raw) i usporeduje ih
// preko Verifier-a; za svih 10 parova iz pairs.csv pokrece Q5 na sve tri
// varijante i usporeduje duljine puta. Ovo je jedina klasa u projektu koja
// spaja Queries/* i Bench/Verifier.cs u jednu cjelinu.
//
// Broj provjera OSTAJE 50 (10 subjekata x 4 upita + 10 parova x 1 upit),
// NE raste na 150 - svaka provjera INTERNO usporeduje sve tri varijante
// odjednom (Verifier.CompareIdSets/CompareLength s imenovanim skupovima),
// ne tri odvojene parne provjere (v. odluka #2 u uputama).
public static class VerifyRunner
{
    // Vraca exit kod procesa: 0 ako je SVE proslo, 1 ako je bilo sto palo.
    // Plan zabranjuje pokretanje "bench" dok "verify" ne prode cist, pa
    // Program.cs ovaj povratni kod izravno prosljeduje kao exit kod.
    public static async Task<int> RunAsync(AppConfig config, string dataRawDir)
    {
        List<string> subjectIds = SubjectsAndPairsReader.ReadSubjects(dataRawDir);
        List<PairRaw> pairs = SubjectsAndPairsReader.ReadPairs(dataRawDir);

        using var neo4jDriver = GraphDatabase.Driver(
            config.Neo4j.Uri,
            AuthTokens.Basic(config.Neo4j.User, config.Neo4j.Password));

        var mongoClient = new MongoClient(config.Mongo.ConnectionString);
        IMongoDatabase mongoDatabase = mongoClient.GetDatabase(config.Mongo.Database);

        var summary = new VerificationSummary();
        var q5NoteShown = new bool[1]; // jednoclani niz kao mutable "ref bool" zamjena - async metode ne smiju imati ref/out parametre

        Console.WriteLine($"Verify: provjeravam Q1-Q4 za {subjectIds.Count} subjekata (neo4j / mongo / mongo-raw)...");
        foreach (string subjectId in subjectIds)
        {
            await VerifyQ1(neo4jDriver, mongoDatabase, subjectId, summary);
            await VerifyQ2(neo4jDriver, mongoDatabase, subjectId, summary);
            await VerifyQ3(neo4jDriver, mongoDatabase, subjectId, summary);
            await VerifyQ4(neo4jDriver, mongoDatabase, subjectId, summary);
        }

        Console.WriteLine($"Verify: provjeravam Q5 za {pairs.Count} parova (neo4j / mongo / mongo-raw)...");
        foreach (PairRaw pair in pairs)
        {
            await VerifyQ5(neo4jDriver, mongoDatabase, pair, config.Generator.MaxHops, summary, q5NoteShown);
        }

        summary.PrintSummary();
        return summary.AllPassed ? 0 : 1;
    }

    private static async Task VerifyQ1(IDriver neo4j, IMongoDatabase mongo, string subjectId, VerificationSummary summary)
    {
        UserProfile? neo4jResult = await Neo4jQueries.Q1(neo4j, subjectId);
        UserProfile? mongoResult = await MongoQueries.Q1(mongo, subjectId);
        UserProfile? mongoRawResult = await MongoRawQueries.Q1(mongo, subjectId);

        // Q1 vraca NAJVISE jedan korisnik - svodimo na popis od 0 ili 1
        // ID-a da iskoristimo isti CompareIdSets kao ostali upiti, umjesto
        // posebne grane koda samo za ovaj slucaj.
        bool passed = Verifier.CompareIdSets(
            "Q1", subjectId,
            ("neo4j", ToIdList(neo4jResult?.Id)),
            ("mongo", ToIdList(mongoResult?.Id)),
            ("mongo-raw", ToIdList(mongoRawResult?.Id)));
        summary.Record(passed);
    }

    private static async Task VerifyQ2(IDriver neo4j, IMongoDatabase mongo, string subjectId, VerificationSummary summary)
    {
        List<UserSummary> neo4jResult = await Neo4jQueries.Q2(neo4j, subjectId);
        List<UserSummary> mongoResult = await MongoQueries.Q2(mongo, subjectId);
        List<UserSummary> mongoRawResult = await MongoRawQueries.Q2(mongo, subjectId);

        bool passed = Verifier.CompareIdSets(
            "Q2", subjectId,
            ("neo4j", neo4jResult.Select(u => u.Id)),
            ("mongo", mongoResult.Select(u => u.Id)),
            ("mongo-raw", mongoRawResult.Select(u => u.Id)));
        summary.Record(passed);
    }

    private static async Task VerifyQ3(IDriver neo4j, IMongoDatabase mongo, string subjectId, VerificationSummary summary)
    {
        List<UserSummary> neo4jResult = await Neo4jQueries.Q3(neo4j, subjectId);
        List<UserSummary> mongoResult = await MongoQueries.Q3(mongo, subjectId);
        List<UserSummary> mongoRawResult = await MongoRawQueries.Q3(mongo, subjectId);

        bool passed = Verifier.CompareIdSets(
            "Q3", subjectId,
            ("neo4j", neo4jResult.Select(u => u.Id)),
            ("mongo", mongoResult.Select(u => u.Id)),
            ("mongo-raw", mongoRawResult.Select(u => u.Id)));
        summary.Record(passed);
    }

    private static async Task VerifyQ4(IDriver neo4j, IMongoDatabase mongo, string subjectId, VerificationSummary summary)
    {
        List<FeedItem> neo4jResult = await Neo4jQueries.Q4(neo4j, subjectId);
        List<FeedItem> mongoResult = await MongoQueries.Q4(mongo, subjectId);
        List<FeedItem> mongoRawResult = await MongoRawQueries.Q4(mongo, subjectId);

        // FeedItem nema "Id", ima "PostId" - ovdje se to eksplicitno bira
        // (v. komentar u Bench/Verifier.cs zasto to Verifier sam ne pogada).
        bool passed = Verifier.CompareIdSets(
            "Q4", subjectId,
            ("neo4j", neo4jResult.Select(f => f.PostId)),
            ("mongo", mongoResult.Select(f => f.PostId)),
            ("mongo-raw", mongoRawResult.Select(f => f.PostId)));
        summary.Record(passed);
    }

    private static async Task VerifyQ5(
        IDriver neo4j,
        IMongoDatabase mongo,
        PairRaw pair,
        int maxHops,
        VerificationSummary summary,
        bool[] q5NoteShown)
    {
        PathResult neo4jResult = await Neo4jQueries.Q5(neo4j, pair.FromId, pair.ToId, maxHops);
        PathResult mongoResult = await MongoQueries.Q5(mongo, pair.FromId, pair.ToId, maxHops);
        PathResult mongoRawResult = await MongoRawQueries.Q5(mongo, pair.FromId, pair.ToId, maxHops);

        if (!q5NoteShown[0])
        {
            // Ispisano JEDNOM po vožnji, ne po paru - da nitko zeleni
            // rezultat verifikacije ne procita kao dokaz da se PUTEVI
            // podudaraju: CompareLength namjerno usporeduje SAMO duljinu
            // (v. Bench/Verifier.cs), a mongo-raw ($graphLookup) uopce ne
            // vraca put, samo dubinu (v. MongoRawQueries.Q5 XML komentar).
            Console.WriteLine(
                "Verify: napomena - Q5 provjera usporeduje SAMO duljinu puta. " +
                "mongo-raw ($graphLookup) ne vraca sam put (samo dubinu), pa zeleni " +
                "rezultat ovdje NE dokazuje da su putevi (nizovi ID-eva) identicni.");
            q5NoteShown[0] = true;
        }

        string pairLabel = $"{pair.FromId}->{pair.ToId}";
        bool passed = Verifier.CompareLength(
            "Q5", pairLabel,
            ("neo4j", neo4jResult.Length),
            ("mongo", mongoResult.Length),
            ("mongo-raw", mongoRawResult.Length));
        summary.Record(passed);
    }

    private static List<string> ToIdList(string? id) => id is null ? new List<string>() : new List<string> { id };
}
