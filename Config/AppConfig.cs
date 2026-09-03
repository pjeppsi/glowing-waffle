namespace BenchmarkApp.Config;

// Ove klase su samo "kalup" u koji Microsoft.Extensions.Configuration
// automatski upiše vrijednosti iz appsettings.json (po imenu sekcije/polja).
// Nema logike ovdje, samo podaci.

public class GeneratorConfig
{
    public int NUsers { get; set; }
    public int AvgDegree { get; set; }
    public int Seed { get; set; }
    public int SubjectsCount { get; set; }
    public int PairsCount { get; set; }
    public int MaxHops { get; set; }
}

public class BenchConfig
{
    public int Warmup { get; set; }
    public int Measurements { get; set; }
}

public class Neo4jConfig
{
    public string Uri { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
}

public class MongoConfig
{
    public string ConnectionString { get; set; } = "";
    public string Database { get; set; } = "";
}

public class AppConfig
{
    public GeneratorConfig Generator { get; set; } = new();
    public BenchConfig Bench { get; set; } = new();
    public Neo4jConfig Neo4j { get; set; } = new();
    public MongoConfig Mongo { get; set; } = new();
}
