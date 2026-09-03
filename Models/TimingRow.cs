namespace BenchmarkApp.Models;

// Jedan red u results/timings.csv - JEDNO pojedinacno mjerenje jednog
// poziva jednog upita na jednoj bazi. RunIndex je 0..(measurements-1) -
// warmup pozivi se NE upisuju ovdje (v. Bench/Runner.cs), samo stvarna
// mjerenja koja ulaze u medijan/min/max racune.
//
// RowsReturned znaci razlicitu stvar ovisno o upitu: broj vracenih
// korisnika (Q1-Q3), broj objava u feedu (Q4), ili duljina puta (Q5, gdje
// je "stavka" jedan hop) - v. komentar u Runner.cs kod svakog upita.
public record TimingRow(string Query, string Db, string SubjectLabel, int RunIndex, double ElapsedMs, int RowsReturned);
