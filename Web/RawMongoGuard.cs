using MongoDB.Bson;
using MongoDB.Driver;

namespace BenchmarkApp.Web;

// Provodi read-only ogradu za slobodan MQL unos (Faza 5 plana, Odluka #1 u
// uputama) - NIJE opcionalna "provjera stringa kao dodatna ljubaznost",
// nego JEDINA obrana na Mongo strani (za razliku od Cyphera, gdje to radi
// sam Neo4j kroz ExecuteReadAsync - Mongov C# driver nema ekvivalentan
// "read-only transaction" nacin rada za proizvoljne naredbe, pa se ovdje
// mora provjeriti eksplicitno prije izvrsavanja).
//
// Razlog zasto je ovo NUZNO, ne teoretski oprez: tijekom rada na ovom
// projektu je naredba za brisanje po prefiksu vec obrisala stvarne podatke
// u Neo4ju (korisnik u0010000 i dvije objave), sto je zahtijevalo rucnu
// obnovu iz CSV-ova.
//
// Ocekivani ulazni oblik teksta: JSON dokument u stilu Mongovog
// "runCommand" - {"find": "users", "filter": {...}, "limit": 20} ili
// {"aggregate": "users", "pipeline": [...]}. Sve drugo se ODBIJA.
public static class RawMongoGuard
{
    private const int MaxRows = 500; // ista "svaki upit ima LIMIT" logika kao Q1-Q5 (CLAUDE.md)

    public static async Task<RawResponse> ExecuteAsync(IMongoDatabase database, string text)
    {
        BsonDocument command;
        try
        {
            command = BsonDocument.Parse(text);
        }
        catch (Exception ex)
        {
            return new RawResponse(false, null, 0, $"Tekst nije valjan JSON: {ex.Message}");
        }

        if (command.Contains("find"))
        {
            return await ExecuteFindAsync(database, command);
        }

        if (command.Contains("aggregate"))
        {
            return await ExecuteAggregate(database, command);
        }

        return new RawResponse(
            false, null, 0,
            "Odbijeno: dopustene su samo 'find' i 'aggregate' naredbe (npr. " +
            "{\"find\":\"users\",\"filter\":{...}} ili {\"aggregate\":\"users\",\"pipeline\":[...]}). " +
            "Ovo je read-only alat - v. napomena na ekranu.");
    }

    private static async Task<RawResponse> ExecuteFindAsync(IMongoDatabase database, BsonDocument command)
    {
        string collectionName = command["find"].AsString;
        var collection = database.GetCollection<BsonDocument>(collectionName);

        BsonDocument filter = command.TryGetValue("filter", out BsonValue filterValue)
            ? filterValue.AsBsonDocument
            : new BsonDocument();

        List<BsonDocument> results = await collection.Find(filter).Limit(MaxRows).ToListAsync();
        return new RawResponse(true, results.Select(BsonToDict), results.Count, null);
    }

    private static async Task<RawResponse> ExecuteAggregate(IMongoDatabase database, BsonDocument command)
    {
        string collectionName = command["aggregate"].AsString;

        if (!command.TryGetValue("pipeline", out BsonValue pipelineValue) || !pipelineValue.IsBsonArray)
        {
            return new RawResponse(false, null, 0, "Odbijeno: 'aggregate' naredba mora imati 'pipeline' niz.");
        }

        var stages = pipelineValue.AsBsonArray.Select(s => s.AsBsonDocument).ToList();

        // JEDINA prava obrana kod aggregate-a: $out i $merge PISU u bazu
        // (kreiraju/prepisuju kolekciju) - sve ostalo u agregacijskom
        // pipelineu je citanje, bez obzira koliko slozeno.
        foreach (BsonDocument stage in stages)
        {
            if (stage.Contains("$out") || stage.Contains("$merge"))
            {
                return new RawResponse(
                    false, null, 0,
                    "Odbijeno: pipeline sadrzi '$out' ili '$merge' - te faze PISU u bazu, " +
                    "a ovaj alat je read-only.");
            }
        }

        var collection = database.GetCollection<BsonDocument>(collectionName);
        List<BsonDocument> results = await collection.Aggregate<BsonDocument>(stages).ToListAsync();

        // Ako korisnikov pipeline sam nije stavio $limit, rezultat ovdje
        // svejedno rezemo na MaxRows za prikaz - "svaki upit ima LIMIT"
        // pravilo vrijedi i za slobodan unos.
        List<BsonDocument> limited = results.Take(MaxRows).ToList();
        return new RawResponse(true, limited.Select(BsonToDict), limited.Count, null);
    }

    // BsonDocument se serijalizira u System.Text.Json preko obicnog
    // Dictionary<string, object?> - System.Text.Json ne zna sam serijalizirati
    // BsonDocument/BsonValue tipove.
    private static Dictionary<string, object?> BsonToDict(BsonDocument doc)
    {
        var result = new Dictionary<string, object?>();
        foreach (BsonElement element in doc)
        {
            result[element.Name] = BsonToPlain(element.Value);
        }
        return result;
    }

    private static object? BsonToPlain(BsonValue value) => value.BsonType switch
    {
        BsonType.Null => null,
        BsonType.Array => value.AsBsonArray.Select(BsonToPlain).ToList(),
        BsonType.Document => BsonToDict(value.AsBsonDocument),
        BsonType.ObjectId => value.AsObjectId.ToString(),
        BsonType.DateTime => value.ToUniversalTime(),
        BsonType.Boolean => value.AsBoolean,
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        BsonType.Double => value.AsDouble,
        _ => value.ToString(),
    };
}
