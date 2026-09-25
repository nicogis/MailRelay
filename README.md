# MailRelay

Lightweight Windows SMTP relay written in C#.

MailRelay accepts SMTP messages locally (default `127.0.0.1:2525`) and forwards the complete MIME message through an external SMTP server using Chilkat. It is intended for applications such as SQL Server Database Mail that need to send through an SMTP endpoint using implicit TLS on port 465.

## Flow

```text
SQL Server Database Mail -> SMTP 127.0.0.1:2525 -> MailRelay -> SMTPS :465 -> External SMTP server
```

MIME content is preserved, including HTML and attachments.

## SQL Server Database Mail

- Server: `127.0.0.1`
- Port: `2525`
- SSL: disabled
- Authentication: Anonymous

## Configuration

The repository contains only non-sensitive defaults in `MailRelay/appsettings.json`.

Do **not** commit SMTP passwords, usernames, Chilkat license keys, or other secrets.

### Development: appsettings.Local.json

For local development, create this file:

```text
MailRelay/appsettings.Local.json
```

It is explicitly ignored by Git and is loaded after the normal application configuration, so values in it override `appsettings.json`.

Example:

```json
{
  "Relay": {
    "Username": "smtp-user",
    "Password": "smtp-password",
    "ChilkatLicenseKey": "your-license-key"
  }
}
```

Never remove `appsettings.Local.json` from `.gitignore`.

### Production: environment variables

For a Windows Service, prefer machine-level environment variables rather than a file containing production credentials.

.NET configuration maps double underscores (`__`) to configuration sections:

```text
Relay__Username
Relay__Password
Relay__ChilkatLicenseKey
```

Example PowerShell, run from an elevated shell:

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

The resulting configuration precedence for these values is:

```text
appsettings.json
        ↓ overridden by
appsettings.Local.json (when present)
        ↓ overridden by
Environment variables
```

This means production secrets can stay outside the repository entirely.

## Security

The default listener binds only to `127.0.0.1`, so it is not exposed to the LAN or Internet.

If a secret is accidentally committed, removing it from the latest version of a file is not sufficient: rotate the credential/license key immediately because it remains in Git history.

> The project intentionally does not pin a Chilkat package: add the Chilkat NuGet/reference used by your licensed environment.
