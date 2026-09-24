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

Configure `MailRelay/appsettings.json`. Do not commit real SMTP credentials or Chilkat license keys.

The default listener binds only to `127.0.0.1`, so it is not exposed to the LAN or Internet.

> The project intentionally does not pin a Chilkat package: add the Chilkat NuGet/reference used by your licensed environment.
