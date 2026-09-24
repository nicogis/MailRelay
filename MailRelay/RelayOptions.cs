namespace MailRelay;
public sealed class RelayOptions
{
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; } = 2525;
    public int MaxMessageSizeBytes { get; set; } = 25 * 1024 * 1024;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 465;
    public bool SmtpSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string? ChilkatLicenseKey { get; set; }
}