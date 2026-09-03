using BenchmarkApp.Config;
using BenchmarkApp.Models;

namespace BenchmarkApp.Data;

// Generira sintetičku društvenu mrežu: korisnike, FOLLOWS veze (preferential
// attachment), objave, i dvije pomoćne datoteke koje benchmark koristi za
// odabir "na kome mjerimo" (subjects.csv i pairs.csv).
//
// SVE je deterministički - isti Seed iz appsettings.json uvijek daje ISTI
// graf, iste postove, iste subjekte i iste parove. To je bitno jer i
// Neo4jLoader i MongoLoader čitaju iste CSV-ove, pa obje baze na kraju
// sadrže identične podatke - to je preduvjet za poštenu usporedbu.
public static class Generator
{
    // Koliko postova (najviše) generiramo po korisniku - konstanta, ne
    // konfiguracijski parametar, jer plan ne traži da broj postova bude
    // podesiv, samo da bude realan i da Q4 (feed) ima što sortirati.
    private const int MinPostsPerUser = 0;
    private const int MaxPostsPerUser = 8;

    // Fiksna "nulta točka" za datume - NE DateTime.UtcNow. Kad bismo datume
    // računali od "sada", generator bi svaki put (danas, za mjesec dana...)
    // proizveo drukčije apsolutne datume iako je Seed isti, jer bi polazna
    // točka plutala. Ovako je generator potpuno reproducibilan bez obzira
    // kad se pokrene.
    private static readonly DateTime Epoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const int DateSpreadDays = 730; // objave/registracije razbacane kroz otprilike 2 godine

    private static readonly string[] FirstNames =
    {
        "Ana", "Ivan", "Marko", "Petra", "Luka", "Ema", "Filip", "Mia",
        "Josip", "Lana", "Dario", "Nika", "Toni", "Sara", "Bruno", "Iva",
        "Karlo", "Ines", "Leon", "Dora"
    };

    private static readonly string[] LastNames =
    {
        "Horvat", "Kovac", "Babic", "Maric", "Novak", "Juric", "Kovacevic",
        "Vukovic", "Knezevic", "Matic", "Radic", "Simic", "Pavlovic",
        "Perkovic", "Kolar", "Zoric", "Vidovic", "Barisic", "Curic", "Sever"
    };

    private static readonly string[] PostSentences =
    {
        "Danas je bio odlican dan za setnju",
        "Upravo sam zavrsio zanimljivu knjigu",
        "Kava ujutro je najbolji dio dana",
        "Radim na novom projektu i uzivam",
        "Vrijeme se napokon smirilo",
        "Isprobao sam novi recept za veceru",
        "Gledao sam sjajan film sinoc",
        "Planiram putovanje sljedeci mjesec",
        "Trening je danas prosao odlicno",
        "Uzivam u ovoj glazbi cijeli tjedan"
    };

    public static void Run(AppConfig config, string outputDir)
    {
        var gen = config.Generator;
        var rng = new Random(gen.Seed);

        Directory.CreateDirectory(outputDir);

        Console.WriteLine($"Generiram {gen.NUsers} korisnika (seed={gen.Seed}, avg_degree={gen.AvgDegree})...");
        var users = GenerateUsers(gen.NUsers, rng);

        Console.WriteLine("Generiram FOLLOWS graf preferential attachmentom...");
        var (following, followers) = GeneratePreferentialAttachmentGraph(gen.NUsers, gen.AvgDegree, rng);

        Console.WriteLine("Generiram objave...");
        var posts = GeneratePosts(users, rng);

        Console.WriteLine($"Biram {gen.SubjectsCount} nasumicnih subjekata...");
        var subjectIds = PickSubjects(users, gen.SubjectsCount, rng);

        Console.WriteLine($"Tražim {gen.PairsCount} parova s usmjerenim putem <= {gen.MaxHops} hopova...");
        var pairs = FindPairs(users, following, gen.PairsCount, gen.MaxHops, rng);

        WriteUsersCsv(Path.Combine(outputDir, "users.csv"), users);
        WriteFollowsCsv(Path.Combine(outputDir, "follows.csv"), users, following);
        WritePostsCsv(Path.Combine(outputDir, "posts.csv"), posts);
        WriteSubjectsCsv(Path.Combine(outputDir, "subjects.csv"), subjectIds);
        WritePairsCsv(Path.Combine(outputDir, "pairs.csv"), pairs);

        PrintStatistics(users, following, followers, posts, pairs);
    }

    // --- Generiranje korisnika ---

    private static List<UserRaw> GenerateUsers(int n, Random rng)
    {
        var users = new List<UserRaw>(n);
        for (int i = 0; i < n; i++)
        {
            // Id je zero-padded na 7 znamenki (npr. "u0000001") - to znaci
            // da obicno (ordinal, Unicode-codepoint) sortiranje stringova
            // daje ISTI poredak kao sortiranje po broju. To je vazno jer
            // Q2/Q3 upiti sortiraju po Id-u prije rezanja na LIMIT 500, i
            // zelimo da to sortiranje bude jednostavno za razumjeti (nema
            // "u10" < "u2" iznenadenja).
            string id = $"u{(i + 1):D7}";
            string first = FirstNames[rng.Next(FirstNames.Length)];
            string last = LastNames[rng.Next(LastNames.Length)];
            string username = $"{first.ToLowerInvariant()}_{last.ToLowerInvariant()}{i + 1}";
            string name = $"{first} {last}";
            var createdAt = Epoch.AddMinutes(rng.Next(0, DateSpreadDays * 24 * 60));
            users.Add(new UserRaw(id, username, name, createdAt));
        }
        return users;
    }

    // --- Preferential attachment graf (FOLLOWS) ---
    //
    // Zamisao (Barabasi-Albert stil, prilagodeno na usmjereni "follow" graf):
    // korisnici se dodaju jedan po jedan, redom 0..n-1. Kad se doda korisnik
    // i, on prati `avgDegree` VEC POSTOJECIH korisnika (0..i-1). Ti se
    // korisnici NE biraju uniformno nasumicno, nego s vjerojatnoscu
    // proporcionalnom njihovom TRENUTNOM broju pratitelja (in-degree) -
    // popularniji korisnik ima vecu sansu da ga novi korisnik zaprati.
    // To je "rich get richer" mehanizam koji proizvodi realisticnu,
    // power-law-oblik raspodjelu broja pratitelja (par "hub" korisnika s
    // puno pratitelja, mnogo korisnika s malo), umjesto ravne raspodjele
    // koju bi dao ciscisto nasumican graf.
    //
    // Tehnicki trik za brzo tezinsko biranje: umjesto da svaki put racunamo
    // vjerojatnosti iz nule (sporo), drzimo "pool" listu u kojoj se svaki
    // korisnik pojavljuje JEDNOM ZA SVAKOG SVOG TRENUTNOG PRATITELJA. Kad
    // nasumicno izvucemo indeks iz te liste, korisnici s vise pratitelja
    // se izvuku cesce - upravo zato sto se u listi pojavljuju vise puta.
    private static (List<int>[] following, List<int>[] followers) GeneratePreferentialAttachmentGraph(
        int n, int avgDegree, Random rng)
    {
        var following = new List<int>[n];
        var followers = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            following[i] = new List<int>();
            followers[i] = new List<int>();
        }

        var pool = new List<int>();

        for (int i = 0; i < n; i++)
        {
            // korisnik i moze pratiti najvise i postojecih korisnika
            // (prvi korisnik, i=0, jos nema koga pratiti)
            int m = Math.Min(avgDegree, i);
            if (m > 0)
            {
                var targets = PickPreferentialTargets(i, m, pool, rng);
                foreach (int t in targets)
                {
                    following[i].Add(t);
                    followers[t].Add(i);
                    pool.Add(t); // t sad ima jednog pratitelja vise -> veca sansa da ga netko odabere idući put
                }
            }

            // Svaki korisnik, cim je "roden", dobiva JEDAN osnovni (baseline)
            // upis u pool - cak i prije nego ga itko zaprati. Ovo je namjerno
            // DODANO NAKON sto je i odabrao SVOJE mete (ne prije), iz dva
            // razloga: (1) tako pool tijekom i-ovog vlastitog biranja jos
            // uvijek sadrzi samo korisnike 0..i-1, sto sprjecava da i
            // slucajno zaprati samog sebe; (2) matematicki, ovo je standardni
            // "+1" u formuli P(odabir cvora j) ~ (indegree(j) + 1) - bez
            // ovog osnovnog uloga, potpuno nov korisnik ima BAS NULTU sansu
            // da ga itko ikad zaprati (nije jos u poolu), pa "bogati" prvih
            // par korisnika iz bootstrap faze zauvijek monopoliziraju sve
            // buduce pratitelje (to se i dogodilo prije ovog ispravka -
            // in-degree max je bio 9999, doslovno SVI su pratili jednog od
            // prvih 15 korisnika). Osnovni ulog to sprjecava: i noviji
            // korisnici imaju nenultu, iako manju, sansu da postanu popularni.
            pool.Add(i);
        }

        return (following, followers);
    }

    private static List<int> PickPreferentialTargets(int newUserIndex, int m, List<int> pool, Random rng)
    {
        var chosen = new HashSet<int>();
        int attempts = 0;
        int maxAttempts = m * 50; // sigurnosna granica protiv beskonacne petlje ako je dedupe uporan

        while (chosen.Count < m && attempts < maxAttempts)
        {
            attempts++;
            // dok je pool prazan (na samom pocetku), biramo uniformno nasumicno
            int candidate = pool.Count > 0 ? pool[rng.Next(pool.Count)] : rng.Next(newUserIndex);
            chosen.Add(candidate);
        }

        // U rijetkom slucaju da dedupe ne uspije skupiti m razlicitih u
        // maxAttempts pokusaja (moguce kad je newUserIndex jako mali),
        // popuni ostatak jednostavnim uniformnim nasumicnim odabirom.
        while (chosen.Count < m && chosen.Count < newUserIndex)
        {
            chosen.Add(rng.Next(newUserIndex));
        }

        return chosen.ToList();
    }

    // --- Objave ---

    private static List<PostRaw> GeneratePosts(List<UserRaw> users, Random rng)
    {
        var posts = new List<PostRaw>();
        int nextPostNumber = 1;
        foreach (var user in users)
        {
            int postCount = rng.Next(MinPostsPerUser, MaxPostsPerUser + 1);
            for (int p = 0; p < postCount; p++)
            {
                string id = $"p{nextPostNumber:D8}";
                nextPostNumber++;
                string text = PostSentences[rng.Next(PostSentences.Length)];
                var createdAt = Epoch.AddMinutes(rng.Next(0, DateSpreadDays * 24 * 60));
                posts.Add(new PostRaw(id, user.Id, text, createdAt));
            }
        }
        return posts;
    }

    // --- Subjekti (Q1-Q4) ---

    private static List<string> PickSubjects(List<UserRaw> users, int count, Random rng)
    {
        var chosenIndices = new HashSet<int>();
        while (chosenIndices.Count < count)
        {
            chosenIndices.Add(rng.Next(users.Count));
        }
        return chosenIndices.Select(i => users[i].Id).ToList();
    }

    // --- Parovi za Q5 (najkraci put) ---
    //
    // Za svaki par MORAMO unaprijed znati da usmjereni put od From do To
    // postoji i da nije dulji od max_hops - inace bismo mogli slucajno
    // odabrati par bez ikakvog puta (obje baze bi samo javile "nema puta",
    // sto ne mjeri traversal) ili par s putem duljim od max_hops (obje baze
    // bi opet javile "nema puta unutar granice", isti problem).
    private static List<PairRaw> FindPairs(
        List<UserRaw> users, List<int>[] following, int pairsCount, int maxHops, Random rng)
    {
        var pairs = new List<PairRaw>();
        var seenPairs = new HashSet<(int from, int to)>();
        int attempts = 0;
        int maxAttempts = pairsCount * 500;

        while (pairs.Count < pairsCount && attempts < maxAttempts)
        {
            attempts++;
            int from = rng.Next(users.Count);
            var distances = BfsDistances(from, following, maxHops);

            // makni sam "from" (udaljenost 0 - to nije koristan par)
            var reachable = distances.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
            if (reachable.Count == 0) continue;

            int to = reachable[rng.Next(reachable.Count)];
            if (!seenPairs.Add((from, to))) continue;

            pairs.Add(new PairRaw(users[from].Id, users[to].Id, distances[to]));
        }

        if (pairs.Count < pairsCount)
        {
            throw new InvalidOperationException(
                $"Generator nije uspio pronaci {pairsCount} parova s usmjerenim putem <= {maxHops} " +
                $"hopova nakon {maxAttempts} pokusaja. Povecajte avg_degree ili max_hops u appsettings.json.");
        }

        return pairs;
    }

    // Standardni BFS (breadth-first search) po usmjerenim FOLLOWS bridovima,
    // ogranicen na najvise maxHops koraka. Vraca rjecnik {indeks cvora ->
    // udaljenost od "from"} za sve cvorove dosegnute unutar te granice.
    // BFS po definiciji prvi put pronalazi svaki cvor preko NAJKRACEG puta,
    // pa je vrijednost u rjecniku ujedno i duljina najkraceg puta.
    private static Dictionary<int, int> BfsDistances(int from, List<int>[] following, int maxHops)
    {
        var dist = new Dictionary<int, int> { [from] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            int currentDist = dist[current];
            if (currentDist >= maxHops) continue; // ne siri dalje od dozvoljene granice

            foreach (int next in following[current])
            {
                if (!dist.ContainsKey(next))
                {
                    dist[next] = currentDist + 1;
                    queue.Enqueue(next);
                }
            }
        }

        return dist;
    }

    // --- Pisanje CSV datoteka ---
    //
    // Namjerno JEDNOSTAVAN CSV bez citatnika/escapinga - Username, Name i
    // Text su generirani iz fiksnih popisa rijeci BEZ zareza i navodnika,
    // pa obicno spajanje stringova zarezom ovdje ne moze pokvariti format.
    // Da su podaci dolazili od stvarnog korisnickog unosa, trebao bi pravi
    // CSV writer s escapingom - ovdje bi to bila nepotrebna slozenost.

    private static void WriteUsersCsv(string path, List<UserRaw> users)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("Id,Username,Name,CreatedAt");
        foreach (var u in users)
        {
            writer.WriteLine($"{u.Id},{u.Username},{u.Name},{u.CreatedAt:O}");
        }
    }

    private static void WriteFollowsCsv(string path, List<UserRaw> users, List<int>[] following)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("FromId,ToId");
        for (int i = 0; i < following.Length; i++)
        {
            foreach (int t in following[i])
            {
                writer.WriteLine($"{users[i].Id},{users[t].Id}");
            }
        }
    }

    private static void WritePostsCsv(string path, List<PostRaw> posts)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("Id,AuthorId,Text,CreatedAt");
        foreach (var p in posts)
        {
            writer.WriteLine($"{p.Id},{p.AuthorId},{p.Text},{p.CreatedAt:O}");
        }
    }

    private static void WriteSubjectsCsv(string path, List<string> subjectIds)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("Id");
        foreach (var id in subjectIds)
        {
            writer.WriteLine(id);
        }
    }

    private static void WritePairsCsv(string path, List<PairRaw> pairs)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("FromId,ToId,ExpectedLength");
        foreach (var pair in pairs)
        {
            writer.WriteLine($"{pair.FromId},{pair.ToId},{pair.ExpectedLength}");
        }
    }

    // --- Statistika za konzolu ---

    private static void PrintStatistics(
        List<UserRaw> users, List<int>[] following, List<int>[] followers,
        List<PostRaw> posts, List<PairRaw> pairs)
    {
        int edgeCount = following.Sum(f => f.Count);

        Console.WriteLine();
        Console.WriteLine("=== Statistika generiranog grafa ===");
        Console.WriteLine($"Broj korisnika:     {users.Count}");
        Console.WriteLine($"Broj FOLLOWS veza:  {edgeCount}");
        Console.WriteLine($"Broj objava:        {posts.Count}");
        Console.WriteLine();

        var outDegrees = following.Select(f => f.Count).ToArray();
        var inDegrees = followers.Select(f => f.Count).ToArray();

        PrintDegreeDistribution("Izlazni stupanj (koga korisnik prati)", outDegrees);
        PrintDegreeDistribution("Ulazni stupanj (koliko pratitelja korisnik ima)", inDegrees);

        Console.WriteLine();
        Console.WriteLine("=== pairs.csv (10 parova za Q5) ===");
        Console.WriteLine("FromId      ToId        ExpectedLength");
        foreach (var pair in pairs)
        {
            Console.WriteLine($"{pair.FromId}  {pair.ToId}  {pair.ExpectedLength}");
        }
    }

    private static void PrintDegreeDistribution(string label, int[] degrees)
    {
        var sorted = degrees.OrderBy(d => d).ToArray();
        double avg = sorted.Average();
        double median = sorted.Length % 2 == 0
            ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0
            : sorted[sorted.Length / 2];

        Console.WriteLine($"{label}:");
        Console.WriteLine($"  min={sorted[0]}  medijan={median}  prosjek={avg:F2}  max={sorted[^1]}");
    }
}
