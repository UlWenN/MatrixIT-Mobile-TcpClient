using SQLite;

namespace MatrixIT_Mobile;

[Table("Logs")]
public sealed class LogMessage
{
    [PrimaryKey, AutoIncrement, Column("id")]
    public int Id { get; set; }

    [Column("text")]
    public string Text { get; set; } = string.Empty;

    [Column("timestamp")]
    public DateTime Timestamp { get; set; }

    [Column("is_sent_by_us")]
    public bool IsSentByUs { get; set; }

    // сообщения показываются на экране, но не сохраняются в 
    [Ignore]
    public bool IsSystem { get; set; }

    [Ignore]
    public string Sender =>
        IsSystem
            ? "Система"
            : IsSentByUs
                ? "Мы"
                : "Сервер";

    [Ignore]
    public string DisplayTime => Timestamp.ToString("HH:mm:ss");
}