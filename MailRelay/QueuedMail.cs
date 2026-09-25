namespace MailRelay;

public sealed class QueuedMail
{
    public required string Id { get; init; }
    public DateTime CreatedUtc { get; init; }
    public required string EnvelopeFrom { get; init; }
    public required List<string> Recipients { get; init; }
    public int Attempts { get; set; }
    public DateTime NextAttemptUtc { get; set; }
    public string? LastError { get; set; }
}
