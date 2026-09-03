using BenchmarkApp.Models;

namespace BenchmarkApp.Data;

// Cita subjects.csv (10 nasumicnih ID-eva za Q1-Q4) i pairs.csv (10
// provjerenih parova za Q5) - koriste ga i Bench/VerifyRunner.cs i
// Bench/Runner.cs, pa je izdvojeno ovdje umjesto kopirano na oba mjesta.
public static class SubjectsAndPairsReader
{
    public static List<string> ReadSubjects(string dataRawDir)
    {
        string path = Path.Combine(dataRawDir, "subjects.csv");
        return CsvReader.ReadRows(path).Select(row => row[0]).ToList();
    }

    public static List<PairRaw> ReadPairs(string dataRawDir)
    {
        string path = Path.Combine(dataRawDir, "pairs.csv");
        var result = new List<PairRaw>();

        foreach (string[] row in CsvReader.ReadRows(path))
        {
            // Stupci: FromId,ToId,ExpectedLength
            result.Add(new PairRaw(row[0], row[1], int.Parse(row[2])));
        }

        return result;
    }
}
