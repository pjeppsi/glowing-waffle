using BenchmarkApp.Models;

namespace BenchmarkApp.Load;

// Dodaje jedan red u results/db_stats.csv. I Neo4jLoader i MongoLoader
// zovu ovo nakon sto zavrse punjenje - datoteka na kraju ima po jedan red
// za svaku bazu (Tablica 3 u planu).
public static class DbStatsWriter
{
    public static void AppendRow(string resultsDir, DbStats stats)
    {
        Directory.CreateDirectory(resultsDir);
        string path = Path.Combine(resultsDir, "db_stats.csv");

        // Zaglavlje pisemo samo ako datoteka jos ne postoji - load-neo4j i
        // load-mongo se pokrecu kao dvije odvojene naredbe (Program.cs), pa
        // druga naredba MORA dopisati svoj red, ne prepisati prvi.
        bool needsHeader = !File.Exists(path);

        using var writer = new StreamWriter(path, append: true);
        if (needsHeader)
        {
            writer.WriteLine("db,load_time_sec,disk_mb");
        }

        writer.WriteLine($"{stats.Db},{stats.LoadTimeSec:F3},{stats.DiskMb:F1}");
    }
}
