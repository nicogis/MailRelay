namespace MailRelay;

public sealed class RelayOptions
{
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; } = 2525;
    public int MaxMessageSizeBytes { get; set; } = 25 * 1024 * 1024;

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 465;
    public bool SmtpSsl { get; set; } = true;
    public bool SmtpStartTls { get; set; }
    public string AuthenticationMode { get; set; } = "Password";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public OAuthOptions OAuth { get; set; } = new();
    public string? ChilkatLicenseKey { get; set; }

    public string QueueDirectory { get; set; } = "";
    public int RetryBaseSeconds { get; set; } = 60;
    public int RetryMaxSeconds { get; set; } = 3600;
    public int MaxRetryAttempts { get; set; }
}

public sealed class OAuthOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Scope { get; set; } =
        "https://outlook.office365.com/.default";
}
