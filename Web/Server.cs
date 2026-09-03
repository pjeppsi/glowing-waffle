using BenchmarkApp.Config;
using MongoDB.Driver;
using Neo4j.Driver;

namespace BenchmarkApp.Web;

// "dotnet run -- serve" - ASP.NET Core minimal API + staticki wwwroot/index.html.
// Namjerno u ISTOM konzolnom projektu (Sdk.Web, v. BenchmarkApp.csproj) da
// se Queries/* moze koristiti izravno, bez dupliciranja u zaseban web projekt.
public static class Server
{
    public static async Task RunAsync(AppConfig config, string dataRawDir, string resultsDir)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // AppContext.BaseDirectory (ne cwd ljuske) - ovdje Sdk.Web pri
            // buildu kopira wwwroot/, isto nacelo kao appsettings.json u
            // Program.cs. Osigurava da staticke datoteke rade identicno bez
            // obzira odakle je "dotnet run -- serve" pokrenut.
            ContentRootPath = AppContext.BaseDirectory,
            Args = Array.Empty<string>(),
        });

        // Sami odlucujemo port (8080) umjesto da se oslanjamo na
        // ASPNETCORE_URLS - deterministicno i za lokalno pokretanje (host)
        // i za docker-compose (koji mapira "127.0.0.1:8080:8080", v. Faza 6
        // i Faza 5 "servis se ne smije izlagati izvan localhosta" pravilo).
        // "0.0.0.0" unutra je nuzan (spremnik mora primati konekcije na
        // svim svojim sucelja) - VANJSKO ogranicenje na 127.0.0.1 radi
        // docker-compose port mapping, ne ovaj bind.
        builder.WebHost.UseUrls("http://0.0.0.0:8080");

        var app = builder.Build();
        app.MapStaticAssets(); // wwwroot/index.html + prateci JS/CSS (staticwebassets manifest, .NET 9+)

        // JEDAN Neo4j IDriver i JEDAN Mongo klijent za cijeli zivotni vijek
        // servisa - isto nacelo kao Bench/Runner.cs (jeftini omotaci oko
        // connection poola, ne otvaraju konekciju po zahtjevu).
        using IDriver neo4jDriver = GraphDatabase.Driver(
            config.Neo4j.Uri,
            AuthTokens.Basic(config.Neo4j.User, config.Neo4j.Password));

        var mongoClient = new MongoClient(config.Mongo.ConnectionString);
        IMongoDatabase mongoDatabase = mongoClient.GetDatabase(config.Mongo.Database);

        app.MapGet("/", () => Results.Redirect("/index.html"));

        Endpoints.MapApi(app, config, dataRawDir, neo4jDriver, mongoDatabase);

        // Provjeri jesu li baze napunjene i napuni ih ako nisu - da
        // "docker compose up -d" na praznom stroju dade upotrebljivo
        // sucelje bez rucnih koraka. Puni SAMO bazu koja je stvarno prazna
        // (loaderi brisu prije punjenja - v. Web/Seeder.cs).
        //
        // Ide PRIJE app.RunAsync() namjerno: sucelje se ne smije poceti
        // posluzivati dok baze nisu spremne, inace bi prvi korisnikov klik
        // dobio praznu ili polupunu bazu bez ikakvog objasnjenja.
        await Seeder.EnsureLoadedAsync(config, dataRawDir, resultsDir, neo4jDriver, mongoDatabase);

        Console.WriteLine("Serve: sucelje na http://localhost:8080 (Ctrl+C za zaustavljanje).");
        Console.WriteLine($"Serve: Neo4j={config.Neo4j.Uri}, Mongo={config.Mongo.ConnectionString}/{config.Mongo.Database}");
        Console.WriteLine($"Serve: data/raw citam iz {dataRawDir}");

        await app.RunAsync();
    }
}
