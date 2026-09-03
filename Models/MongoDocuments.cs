using MongoDB.Bson.Serialization.Attributes;

namespace BenchmarkApp.Models;

// Oblik dokumenata KAKVI STVARNO LEZE u Mongo kolekcijama - razlicito od
// ostalih Models/*.cs, koji su oblici ULAZA (Raw) i IZLAZA upita
// (Profile/Summary/...). [BsonId] oznacava da se ovo polje sprema kao
// Mongov "_id" (primarni kljuc, automatski indeksiran).
//
// Zive u Models/ (ne u Load/) jer ih koriste I MongoLoader.cs (Faza 2,
// pise ih) I MongoQueries.cs (Faza 4, cita ih natrag) - Models/ je
// neutralno mjesto koje oboje vec referenciraju.

public class UserDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    [BsonElement("username")]
    public string Username { get; set; } = "";

    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("created_at")]
    public DateTime CreatedAt { get; set; }

    // Denormalizirano; u Neo4j modelu su ovo [:FOLLOWS], ovdje su
    // popisi id-eva izravno na dokumentu.
    [BsonElement("following")]
    public List<string> Following { get; set; } = new();

    [BsonElement("followers")]
    public List<string> Followers { get; set; } = new();
}

public class PostDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    [BsonElement("author_id")]
    public string AuthorId { get; set; } = "";

    [BsonElement("text")]
    public string Text { get; set; } = "";

    [BsonElement("created_at")]
    public DateTime CreatedAt { get; set; }
}
