using BenchmarkApp.Config;
using BenchmarkApp.Data;
using BenchmarkApp.Load;
using MongoDB.Bson;
using MongoDB.Driver;
using Neo4j.Driver;

namespace BenchmarkApp.Web;

// Pri pokretanju "serve" provjeri jesu li baze napunjene i, ako NISU, napuni
// ih - da "docker compose up -d" na praznom stroju dade upotrebljivo sucelje
// bez rucnih koraka.
//
// KLJUCNA SIGURNOSNA OGRADA: Neo4jLoader i MongoLoader PRVO OBRISU sve
// postojece podatke pa tek onda pune (moraju, da budu idempotentni). Zato se
// loader smije pozvati SAMO za bazu za koju je potvrdeno da je PRAZNA. Nikad
// se ne puni baza koja vec ima podatke - to bi znacilo tiho brisanje tudeg
// (mozda satima punjenog) dataseta pri obicnom restartu spremnika.
public static class Seeder
{
    // Neo4j nakon pokretanja spremnika treba ~20-30 s dok bolt ne pocne
    // primati konekcije, a docker-compose "depends_on" ceka samo da se
    // spremnik POKRENE, ne i da je servis spreman. Bez ovog cekanja bi
    // "serve" u compose okruzenju pao ili prijavio praznu bazu prije nego
    // Neo4j uopce stigne odgovoriti.
    private const int ReadinessTimeoutSeconds = 120;
    private const int RetryDelaySeconds = 3;

    public static async Task EnsureLoadedAsync(
        AppConfig config,
        string dataRawDir,
        string resultsDir,
        IDriver neo4jDriver,
        IMongoDatabase mongoDatabase)
    {
        Console.WriteLine("Seed: cekam da baze prihvate konekcije...");
        bool ready = await WaitForDatabasesAsync(neo4jDriver, mongoDatabase);
        if (!ready)
        {
            Console.WriteLine(
                $"Seed: baze nisu odgovorile unutar {ReadinessTimeoutSeconds} s - preskacem provjeru punjenja. " +
                "Sucelje ce se svejedno podici, ali upiti ce javljati gresku dok baze ne budu dostupne.");
            return;
        }

        long neo4jUsers = await CountNeo4jUsersAsync(neo4jDriver);
        long mongoUsers = await CountMongoUsersAsync(mongoDatabase);

        Console.WriteLine($"Seed: zatecen broj korisnika - Neo4j={neo4jUsers}, Mongo={mongoUsers}.");

        if (neo4jUsers > 0 && mongoUsers > 0)
        {
            Console.WriteLine("Seed: obje baze su napunjene, ne diram ih.");
            return;
        }

        // Loaderi citaju iskljucivo CSV-ove iz data/raw - bez njih se nema
        // sto puniti. U spremniku je taj direktorij montiran READ-ONLY
        // (v. docker-compose.yml), pa se generiranje NE radi ovdje: CSV-ovi
        // su izvor istine za OBJE baze i namjerno nastaju na hostu naredbom
        // "dotnet run -- generate", da spremnik nikad ne pise u izvorno
        // stablo projekta.
        if (!RawCsvFilesExist(dataRawDir, out string missing))
        {
            Console.WriteLine(
                $"Seed: nedostaje '{missing}' u {dataRawDir} - ne mogu puniti. " +
                "Pokreni na hostu: cd BenchmarkApp && dotnet run -- generate");
            return;
        }

        if (neo4jUsers == 0 && mongoUsers > 0)
        {
            Console.WriteLine(
                "Seed: UPOZORENJE - Neo4j je prazan, a Mongo nije. Punim samo Neo4j. " +
                "Ako Mongo sadrzi podatke iz drukcijeg seeda, baze nece biti usporedive - " +
                "u tom slucaju rucno pokreni oba loadera s hosta.");
        }
        else if (mongoUsers == 0 && neo4jUsers > 0)
        {
            Console.WriteLine(
                "Seed: UPOZORENJE - Mongo je prazan, a Neo4j nije. Punim samo Mongo. " +
                "Ista napomena kao gore vrijedi i ovdje.");
        }

        if (neo4jUsers == 0)
        {
            Console.WriteLine("Seed: punim Neo4j...");
            await Neo4jLoader.RunAsync(config, dataRawDir, resultsDir);
        }

        if (mongoUsers == 0)
        {
            Console.WriteLine("Seed: punim Mongo...");
            await MongoLoader.RunAsync(config, dataRawDir, resultsDir);
        }

        Console.WriteLine("Seed: gotovo.");
    }

    private static async Task<bool> WaitForDatabasesAsync(IDriver neo4jDriver, IMongoDatabase mongoDatabase)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(ReadinessTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            bool neo4jOk = await TryAsync(async () =>
            {
                await using var session = neo4jDriver.AsyncSession();
                await session.ExecuteReadAsync(async tx =>
                {
                    var cursor = await tx.RunAsync("RETURN 1");
                    return await cursor.ToListAsync();
                });
            });

            bool mongoOk = await TryAsync(async () =>
                await mongoDatabase.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1)));

            if (neo4jOk && mongoOk)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds));
        }

        return false;
    }

    private static async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch
        {
            return false; // baza jos nije spremna - petlja gore ce pokusati ponovno
        }
    }

    private static async Task<long> CountNeo4jUsersAsync(IDriver driver)
    {
        await using var session = driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("MATCH (u:User) RETURN count(u) AS n");
            var records = await cursor.ToListAsync();
            return records.Count == 0 ? 0L : records[0]["n"].As<long>();
        });
    }

    private static async Task<long> CountMongoUsersAsync(IMongoDatabase database)
    {
        var users = database.GetCollection<BsonDocument>("users");
        return await users.CountDocumentsAsync(new BsonDocument());
    }

    private static bool RawCsvFilesExist(string dataRawDir, out string missing)
    {
        foreach (string name in new[] { "users.csv", "follows.csv", "posts.csv", "subjects.csv", "pairs.csv" })
        {
            if (!File.Exists(Path.Combine(dataRawDir, name)))
            {
                missing = name;
                return false;
            }
        }

        missing = "";
        return true;
    }
}
