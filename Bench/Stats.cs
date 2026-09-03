namespace BenchmarkApp.Bench;

// Sitne statisticke funkcije nad popisom brojeva - koriste se za racunanje
// tablica u Runner.cs (medijan/min/max vremena, prosjek broja vracenih
// redaka). Namjerno pisano rucno, ne preko vanjske statisticke biblioteke -
// ove formule su jednostavne i vrijedi ih moci procitati direktno u kodu.
public static class Stats
{
    // Medijan = srednja vrijednost SORTIRANOG popisa. Kod parnog broja
    // elemenata (nas slucaj - measurements=20 je paran) uzima se prosjek
    // dva srednja elementa - standardna definicija medijana.
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            throw new InvalidOperationException("Ne mogu izracunati medijan praznog popisa.");
        }

        List<double> sorted = values.OrderBy(v => v).ToList();
        int middle = sorted.Count / 2;

        return sorted.Count % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }

    // 95. percentil - "nearest-rank" metoda (jednostavna, bez interpolacije):
    // sortiraj, uzmi element na indeksu ceil(0.95 * n) - 1. CLAUDE.md trazi
    // p95 uz medijan/min - v. NOTES.md stavka 9 (prije rucno racunato izvan
    // koda, sad dio Tablice 1/2 i summary.md).
    public static double P95(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            throw new InvalidOperationException("Ne mogu izracunati p95 praznog popisa.");
        }

        List<double> sorted = values.OrderBy(v => v).ToList();
        int rank = (int)Math.Ceiling(0.95 * sorted.Count);
        int index = Math.Clamp(rank - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    public static double Min(IReadOnlyList<double> values) => values.Min();

    public static double Max(IReadOnlyList<double> values) => values.Max();

    public static double Average(IReadOnlyList<double> values) => values.Average();

    public static double Average(IReadOnlyList<int> values) => values.Average();
}
