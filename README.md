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

## SQL Server Database Mail

- Server: `127.0.0.1`
- Port: `2525`
- SSL: disabled
- Authentication: Anonymous

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

## Security

The default SMTP listener binds only to `127.0.0.1`, so it is not exposed to the LAN or Internet.

If a secret is accidentally committed, rotate it immediately because removing it from the latest file does not remove it from Git history.
