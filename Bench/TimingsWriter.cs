using BenchmarkApp.Models;

namespace BenchmarkApp.Bench;

// Pise results/timings.csv - SIROV, POTPUN ispis svakog pojedinacnog
// mjerenja. Namjerno se ne agregira ovdje (to rade tablice u konzoli/
// summary.md) - ovaj CSV ostaje sirovina za bilo koju kasniju analizu
// koju korisnik zeli napraviti rucno (npr. graf u Excelu), pa mora sadrzavati
// SVAKI redak, ne samo sazetak.
public static class TimingsWriter
{
    public static void Write(string resultsDir, List<TimingRow> rows)
    {
        Directory.CreateDirectory(resultsDir);
        string path = Path.Combine(resultsDir, "timings.csv");

        // "bench" se pokrece kao JEDNA cjelovita vožnja (za razliku od
        // load-neo4j/load-mongo koji su dvije odvojene naredbe) - zato
        // ovdje PREPISUJEMO cijelu datoteku (ne dopisujemo), svaka nova
        // "dotnet run -- bench" vožnja daje potpuno nov, samostalan CSV.
        using var writer = new StreamWriter(path, append: false);
        writer.WriteLine("query,db,subject,run_index,elapsed_ms,rows_returned");

        foreach (TimingRow row in rows)
        {
            writer.WriteLine(
                $"{row.Query},{row.Db},{row.SubjectLabel},{row.RunIndex}," +
                $"{row.ElapsedMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}," +
                $"{row.RowsReturned}");
        }
    }
}
