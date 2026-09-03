using BenchmarkApp.Bench;
using BenchmarkApp.Config;
using BenchmarkApp.Data;
using BenchmarkApp.Load;
using Microsoft.Extensions.Configuration;

// Ulazna tocka aplikacije. Ceo benchmark je JEDAN konzolni projekt s
// podnaredbama - dotnet run -- <podnaredba> - umjesto vise odvojenih
// projekata, da je jednostavnije za pokretati i pratiti.

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

// appsettings.json se ucitava relativno na direktorij u kojem se nalazi
// .dll/.exe (ne na trenutni radni direktorij ljuske), zato koristimo
// AppContext.BaseDirectory - to je ono sto csproj kopira appsettings.json
// pored pri buildu (v. BenchmarkApp.csproj).
// .AddEnvironmentVariables() dodano za Fazu 6 (Docker) - docker-compose.yml
// nadjacava Neo4j__Uri i Mongo__ConnectionString (dvostruka podvlaka je
// ASP.NET Core konvencija za ugnijezdene kljuceve) da spremnik moze gadati
// "neo4j"/"mongo" servisna imena umjesto "localhost" iz appsettings.json -
// unutar Docker mreze "localhost" je sam spremnik s aplikacijom, ne baze.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var config = new AppConfig();
configuration.Bind(config);

// data/ i results/ direktoriji su relativni na trenutni radni direktorij
// ljuske (odakle se pokrece "dotnet run"), ocekivano BenchmarkApp/ -
// NE na build output direktorij (bin/Debug/net10.0/), jer generirane CSV
// datoteke zelimo vidjeti odmah pored izvornog koda, ne zakopane u bin/.
string dataRawDir = Path.Combine(Directory.GetCurrentDirectory(), "data", "raw");
string resultsDir = Path.Combine(Directory.GetCurrentDirectory(), "results");

string command = args[0];
switch (command)
{
    case "generate":
        Generator.Run(config, dataRawDir);
        return 0;

    case "load-neo4j":
        await Neo4jLoader.RunAsync(config, dataRawDir, resultsDir);
        return 0;

    case "load-mongo":
        await MongoLoader.RunAsync(config, dataRawDir, resultsDir);
        return 0;

    case "verify":
        return await VerifyRunner.RunAsync(config, dataRawDir);

    case "bench":
        return await Runner.RunAsync(config, dataRawDir, resultsDir);

    case "serve":
        // "args" ovdje su NASE podnaredbe (npr. "serve"), ne WebApplication
        // argumenti - namjerno se NE prosljeduju dalje (Array.Empty), da
        // "serve" u ostatku svog niza argumenata ne bude protumacen kao
        // ASP.NET Core host opcija.
        await BenchmarkApp.Web.Server.RunAsync(config, dataRawDir, resultsDir);
        return 0;

    default:
        Console.WriteLine($"Nepoznata podnaredba: {command}");
        PrintUsage();
        return 1;
}

static void PrintUsage()
{
    Console.WriteLine("Koristenje: dotnet run -- <podnaredba>");
    Console.WriteLine("Podnaredbe: generate | load-neo4j | load-mongo | verify | bench | serve");
}
