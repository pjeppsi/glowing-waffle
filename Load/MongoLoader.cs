using System.Diagnostics;
using BenchmarkApp.Config;
using BenchmarkApp.Data;
using BenchmarkApp.Models;
using MongoDB.Driver;

namespace BenchmarkApp.Load;

// Cita data/raw/*.csv i puni MongoDB: "users" kolekcija (s denormaliziranim
// following/followers popisima) i "posts" kolekcija.
public static class MongoLoader
{
    private const int BatchSize = 2000;

    public static async Task RunAsync(AppConfig config, string dataRawDir, string resultsDir)
    {
        var stopwatch = Stopwatch.StartNew();

        var client = new MongoClient(config.Mongo.ConnectionString);
        IMongoDatabase database = client.GetDatabase(config.Mongo.Database);

        // Isti razlog kao kod Neo4jLoader-a - loader mora biti ponovljiv,
        // zato prije punjenja obrisemo kolekcije iz eventualnog proslog pokretanja.
        Console.WriteLine("Mongo: brisem postojece kolekcije...");
        await database.DropCollectionAsync("users");
        await database.DropCollectionAsync("posts");

        IMongoCollection<UserDocument> usersCollection = database.GetCollection<UserDocument>("users");
        IMongoCollection<PostDocument> postsCollection = database.GetCollection<PostDocument>("posts");

        Console.WriteLine("Mongo: citam CSV-ove...");
        List<UserRaw> users = ReadUsers(dataRawDir);
        List<PostRaw> posts = ReadPosts(dataRawDir);
        List<(string FromId, string ToId)> follows = ReadFollows(dataRawDir);

        Console.WriteLine("Mongo: gradim following/followers popise iz follows.csv...");
        var (following, followers) = BuildFollowLists(follows);

        Console.WriteLine($"Mongo: ucitavam {users.Count} korisnika...");
        await LoadUsers(usersCollection, users, following, followers);

        Console.WriteLine($"Mongo: ucitavam {posts.Count} objava...");
        await LoadPosts(postsCollection, posts);

        // Indeksi se grade NAKON umetanja podataka - za veliki bulk insert je
        // brze jednom izgraditi indeks nad gotovim podacima nego ga odrzavati
        // upis-po-upis (standardna Mongo praksa). Vrijeme gradnje indeksa je
        // ipak ukljuceno u stopwatch dolje, pa usporedba s Neo4jem (koji
        // constraint/indeks gradi PRIJE punjenja, jer mu inace MATCH radi
        // pun sken) ostaje poštena - mjeri se UKUPNO vrijeme "od praznog do
        // spremnog za upite" na obje strane, redoslijed koraka unutar toga
        // nije bitan za taj broj.
        Console.WriteLine("Mongo: kreiram indekse...");
        await CreateIndexes(usersCollection, postsCollection);

        stopwatch.Stop();

        // /data/db je direktorij u kojem sluzbeni mongo image drzi bazu.
        double diskMb = DockerDiskSize.GetDirectorySizeMb("benchmark-mongo", "/data/db");
        DbStatsWriter.AppendRow(resultsDir, new DbStats("mongo", stopwatch.Elapsed.TotalSeconds, diskMb));

        Console.WriteLine(
            $"Mongo: gotovo za {stopwatch.Elapsed.TotalSeconds:F2}s. " +
            $"Zauzece na disku: {diskMb:F1} MB.");
    }

    // Iz plosnate liste (from, to) parova gradi dva rjecnika: tko koga prati
    // (following) i tko koga ima kao pratitelja (followers) - to su tocno
    // ona dva niza koja ce zavrsiti na svakom UserDocument-u.
    private static (Dictionary<string, List<string>> Following, Dictionary<string, List<string>> Followers)
        BuildFollowLists(List<(string FromId, string ToId)> follows)
    {
        var following = new Dictionary<string, List<string>>();
        var followers = new Dictionary<string, List<string>>();

        foreach ((string fromId, string toId) in follows)
        {
            // from_id prati to_id => to_id je u "following" popisu od from_id,
            // i from_id je u "followers" popisu od to_id.
            if (!following.TryGetValue(fromId, out List<string>? followingList))
            {
                followingList = new List<string>();
                following[fromId] = followingList;
            }
            followingList.Add(toId);

            if (!followers.TryGetValue(toId, out List<string>? followersList))
            {
                followersList = new List<string>();
                followers[toId] = followersList;
            }
            followersList.Add(fromId);
        }

        return (following, followers);
    }

    private static async Task LoadUsers(
        IMongoCollection<UserDocument> collection,
        List<UserRaw> users,
        Dictionary<string, List<string>> following,
        Dictionary<string, List<string>> followers)
    {
        foreach (UserRaw[] batch in users.Chunk(BatchSize))
        {
            var documents = batch.Select(u => new UserDocument
            {
                Id = u.Id,
                Username = u.Username,
                Name = u.Name,
                CreatedAt = u.CreatedAt,
                // GetValueOrDefault vraca null pa ?? new() daje prazan popis
                // za korisnike koji nikoga ne prate / nemaju pratitelja (ima
                // ih zbog nacina na koji preferential attachment radi, v.
                // Data/Generator.cs) umjesto da baci gresku.
                Following = following.GetValueOrDefault(u.Id) ?? new List<string>(),
                Followers = followers.GetValueOrDefault(u.Id) ?? new List<string>(),
            }).ToList();

            await collection.InsertManyAsync(documents);
        }
    }

    private static async Task LoadPosts(IMongoCollection<PostDocument> collection, List<PostRaw> posts)
    {
        foreach (PostRaw[] batch in posts.Chunk(BatchSize))
        {
            var documents = batch.Select(p => new PostDocument
            {
                Id = p.Id,
                AuthorId = p.AuthorId,
                Text = p.Text,
                CreatedAt = p.CreatedAt,
            }).ToList();

            await collection.InsertManyAsync(documents);
        }
    }

    private static async Task CreateIndexes(
        IMongoCollection<UserDocument> usersCollection,
        IMongoCollection<PostDocument> postsCollection)
    {
        // _id je vec automatski indeksiran na obje kolekcije (users._id,
        // posts._id) - ne treba ga rucno kreirati.

        // Q4 (feed) filtrira po author_id i sortira po created_at silazno -
        // ovaj compound indeks pokriva oboje ZAJEDNO za JEDNOG autora
        // (napomena u NOTES.md ce zabiljeziti sto se dogodi kad Q4 filtrira
        // po VISE author_id vrijednosti odjednom - v. plan, OGRANICENJA/nalazi).
        var authorCreatedAtIndex = Builders<PostDocument>.IndexKeys
            .Ascending(p => p.AuthorId)
            .Descending(p => p.CreatedAt);
        await postsCollection.Indexes.CreateOneAsync(new CreateIndexModel<PostDocument>(authorCreatedAtIndex));
    }

    private static List<UserRaw> ReadUsers(string dataRawDir)
    {
        string path = Path.Combine(dataRawDir, "users.csv");
        var result = new List<UserRaw>();

        foreach (string[] row in CsvReader.ReadRows(path))
        {
            // Stupci: Id,Username,Name,CreatedAt
            result.Add(new UserRaw(row[0], row[1], row[2], CsvReader.ParseDate(row[3])));
        }

        return result;
    }

    private static List<PostRaw> ReadPosts(string dataRawDir)
    {
        string path = Path.Combine(dataRawDir, "posts.csv");
        var result = new List<PostRaw>();

        foreach (string[] row in CsvReader.ReadRows(path))
        {
            // Stupci: Id,AuthorId,Text,CreatedAt
            result.Add(new PostRaw(row[0], row[1], row[2], CsvReader.ParseDate(row[3])));
        }

        return result;
    }

    private static List<(string FromId, string ToId)> ReadFollows(string dataRawDir)
    {
        string path = Path.Combine(dataRawDir, "follows.csv");
        var result = new List<(string, string)>();

        foreach (string[] row in CsvReader.ReadRows(path))
        {
            // Stupci: FromId,ToId
            result.Add((row[0], row[1]));
        }

        return result;
    }
}
