namespace BenchmarkApp.Models;

// Jedan red u results/db_stats.csv - koliko je trajalo punjenje baze i
// koliko prostora zauzimaju podaci na disku nakon punjenja. Sluzi za
// Tablicu 3 iz plana (usporedba ucitavanja), ne za mjerenje upita.
public record DbStats(string Db, double LoadTimeSec, double DiskMb);
