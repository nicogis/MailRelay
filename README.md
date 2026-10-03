# MailRelay

Lightweight Windows SMTP relay written in C#.

MailRelay accepts SMTP messages locally (default `127.0.0.1:2525`), saves them to a durable local queue, and forwards them through an external SMTP server using Chilkat.

## Flow

```text
SQL Server Database Mail
        |
        | SMTP 127.0.0.1:2525
        v
MailRelay
        |
        +--> durable local queue
        |
        | SMTP / SMTPS
        v
External SMTP server
```

A message is acknowledged to the SMTP client only after it has been persisted to the local queue. If the upstream SMTP server is unavailable, MailRelay keeps the message locally and retries automatically.


## Install as a Windows Service

Publish the application to a stable directory, for example:

```powershell
dotnet publish .\MailRelay\MailRelay.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o C:\Services\MailRelay
```

Open PowerShell **as Administrator** and create the service:

```powershell
sc.exe create MailRelay binPath= "C:\Services\MailRelay\MailRelay.exe" start= auto DisplayName= "Mail Relay"
sc.exe description MailRelay "Local SMTP relay with persistent queue and retry"
sc.exe start MailRelay
```

Verify the service:

```powershell
Get-Service MailRelay
```

Verify that the local SMTP listener is active:

```powershell
Get-NetTCPConnection -LocalPort 2525 -ErrorAction SilentlyContinue
```

The expected listener is `127.0.0.1:2525`.

### Stop and remove the service

```powershell
sc.exe stop MailRelay
sc.exe delete MailRelay
```

Deleting the Windows Service does **not** delete the published application, logs, or queued messages. Remove those directories separately only when they are no longer needed.


## SQL Server Database Mail

MailRelay is useful when SQL Server Database Mail cannot communicate directly with the real SMTP server, for example when the provider requires a TLS mode, authentication mechanism, or OAuth flow that Database Mail cannot handle directly.

Configure the Database Mail account to use MailRelay:

- SMTP server: `127.0.0.1`
- Port: `2525`
- SSL: disabled
- Authentication: Anonymous
- E-mail address: the real sender address

The flow becomes:

```text
sp_send_dbmail
    -> SQL Server Database Mail
    -> SMTP 127.0.0.1:2525
    -> MailRelay durable queue
    -> external SMTP server
    -> recipient
```

Example test from SQL Server:

```sql
EXEC msdb.dbo.sp_send_dbmail
    @profile_name = 'MailRelay',
    @recipients = 'recipient@example.com',
    @subject = 'Test MailRelay',
    @body = 'Test SQL Server -> MailRelay -> external SMTP';
```

Check the most recent Database Mail messages:

```sql
SELECT TOP (20)
    mailitem_id,
    sent_status,
    sent_date,
    recipients,
    subject,
    last_mod_date
FROM msdb.dbo.sysmail_allitems
ORDER BY mailitem_id DESC;
```

Check Database Mail errors:

```sql
SELECT TOP (50)
    log_date,
    event_type,
    description,
    process_id,
    mailitem_id
FROM msdb.dbo.sysmail_event_log
ORDER BY log_date DESC;
```

A `sent` status means SQL Server successfully handed the message to MailRelay. Final delivery to the external SMTP server is then handled by MailRelay and can be verified in its logs and durable queue.

## Persistent logging

MailRelay includes a simple built-in daily file logger and does not require an additional logging package.

Default directory:

```text
C:\ProgramData\MailRelay\Logs
```

Files are named:

```text
mailrelay-YYYYMMDD.log
```

The default retention is 30 days.

Configuration:

```json
"FileLogging": {
  "Enabled": true,
  "Directory": "",
  "RetainedDays": 30
}
```

Leave `Directory` empty to use the default ProgramData location.

## Durable local queue and retry

Default queue directory:

```text
C:\ProgramData\MailRelay\Queue
```

For each queued message MailRelay stores:

- `<id>.eml`: the MIME message
- `<id>.json`: envelope and retry metadata

After a successful delivery both files are deleted.

Retry uses exponential backoff:

```text
60s -> 120s -> 240s -> 480s -> ... -> maximum 3600s
```

Configuration:

```json
"Relay": {
  "QueueDirectory": "",
  "RetryBaseSeconds": 60,
  "RetryMaxSeconds": 3600,
  "MaxRetryAttempts": 0
}
```

`MaxRetryAttempts = 0` means unlimited retries.

If a positive maximum is configured and reached, the message is moved to:

```text
C:\ProgramData\MailRelay\Queue\Failed
```

The queue is persistent across service restarts.

Because queued `.eml` files may contain message bodies and attachments, protect the queue directory with appropriate NTFS permissions.

## Configuration and secrets

The repository contains only non-sensitive defaults in `MailRelay/appsettings.json`.

Do **not** commit SMTP passwords, usernames, Chilkat license keys, or other secrets.

### Development: appsettings.Local.json

For local development, create:

```text
MailRelay/appsettings.Local.json
```

It is ignored by Git and loaded after `appsettings.json`.

Example:

```json
{
  "Relay": {
    "SmtpHost": "mail.example.com",
    "SmtpPort": 465,
    "SmtpSsl": true,
    "Username": "smtp-user",
    "Password": "smtp-password",
    "ChilkatLicenseKey": "your-license-key"
  }
}
```

### Production: environment variables

For a Windows Service, prefer machine-level environment variables:

```text
Relay__Username
Relay__Password
Relay__OAuth__TenantId
Relay__OAuth__ClientId
Relay__OAuth__ClientSecret
Relay__ChilkatLicenseKey
```

Example PowerShell:

```powershell
[Environment]::SetEnvironmentVariable(
    "Relay__Username",
    "smtp-user",
    "Machine")

[Environment]::SetEnvironmentVariable(
    "Relay__Password",
    "smtp-password",
    "Machine")

[Environment]::SetEnvironmentVariable(
    "Relay__ChilkatLicenseKey",
    "your-license-key",
    "Machine")
```

Restart the Windows Service after changing machine-level environment variables.



## OAuth2 / XOAUTH2 SMTP option

MailRelay supports both password authentication and OAuth2/XOAUTH2. Select the mode with `Relay:AuthenticationMode` using either `Password` or `OAuth2`. Chilkat authenticates with OAuth2 by setting `MailMan.OAuth2AccessToken`.

For unattended services, use the OAuth2 **client credentials** flow when the mail provider supports application-only SMTP access. This flow does not require opening a browser: the service requests an access token directly from the provider's token endpoint and then uses the token for SMTP XOAUTH2 authentication.

For Microsoft 365 / Exchange Online, the application must first be authorized for app-only SMTP access in the tenant and for the mailbox that it is allowed to send as. Microsoft supports both Exchange service-principal permission onboarding and the newer Exchange Application RBAC model. Use the model appropriate for the tenant before testing the code below.

Chilkat documents the complete Entra ID / Exchange Online setup here:

- [Sending Office365 Email using SMTP with OAuth2 without Browser Interaction](https://www.chilkatsoft.com/office365_smtp_oauth2_without_browser_interaction.asp)

That guide covers the Entra application registration, the `SMTP.SendAsApp` application permission, admin consent, client secret, Exchange Online service-principal registration, mailbox SendAs/FullAccess permissions, and SMTP AUTH requirements.

### Microsoft 365 example

To switch MailRelay to OAuth2, configure:

```json
{
  "Relay": {
    "SmtpHost": "smtp.office365.com",
    "SmtpPort": 587,
    "SmtpSsl": false,
    "SmtpStartTls": true,
    "AuthenticationMode": "OAuth2",
    "Username": "sender@example.com",
    "Password": "",
    "OAuth": {
      "TenantId": "your-tenant-id",
      "ClientId": "your-client-id",
      "ClientSecret": "",
      "Scope": "https://outlook.office365.com/.default"
    }
  }
}
```

For password-based SMTP, set:

```json
"AuthenticationMode": "Password"
```

and configure `Username` and `Password` as before.

The SMTP endpoint is normally:

```text
smtp.office365.com:587
```

and the token scope for application-only SMTP is:

```text
https://outlook.office365.com/.default
```

Example C# token acquisition using a direct HTTP call:

```csharp
using System.Net.Http.Json;
using System.Text.Json.Serialization;

public sealed class OAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }
}

static async Task<string> GetMicrosoft365AccessTokenAsync(
    string tenantId,
    string clientId,
    string clientSecret,
    CancellationToken cancellationToken)
{
    using var http = new HttpClient();

    using var content = new FormUrlEncodedContent(
        new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = "https://outlook.office365.com/.default",
            ["grant_type"] = "client_credentials"
        });

    using var response = await http.PostAsync(
        $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
        content,
        cancellationToken);

    response.EnsureSuccessStatusCode();

    var token = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(
        cancellationToken: cancellationToken)
        ?? throw new InvalidOperationException(
            "The OAuth token endpoint returned an empty response.");

    return token.AccessToken;
}
```

Use the access token with Chilkat:

```csharp
var accessToken = await GetMicrosoft365AccessTokenAsync(
    tenantId,
    clientId,
    clientSecret,
    cancellationToken);

var mailman = new Chilkat.MailMan
{
    SmtpHost = "smtp.office365.com",
    SmtpPort = 587,
    SmtpSsl = false,
    StartTLS = true,
    SmtpUsername = "sender@example.com",
    SmtpPassword = "",
    OAuth2AccessToken = accessToken
};

if (!mailman.SendEmail(email))
{
    throw new InvalidOperationException(mailman.LastErrorText);
}
```

When `OAuth2AccessToken` is set, Chilkat uses SMTP XOAUTH2 when supported by the server. Leave `SmtpPassword` empty.

MailRelay caches the OAuth2 access token and requests a new one when the cached token is within five minutes of expiry. If token acquisition or SMTP delivery fails, the message remains in the durable queue and follows the normal retry policy.

Store `TenantId`, `ClientId`, and especially `ClientSecret` outside source control, preferably as machine-level environment variables, Windows-protected secrets, or a dedicated secret store. The client secret must never be written to application logs.

## Security

The default SMTP listener binds only to `127.0.0.1`, so it is not exposed to the LAN or Internet.

If a secret is accidentally committed, rotate it immediately because removing it from the latest file does not remove it from Git history.
