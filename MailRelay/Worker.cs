using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace MailRelay;

public sealed partial class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly RelayOptions _options;
    private TcpListener? _listener;

    public Worker(ILogger<Worker> logger, IOptions<RelayOptions> options)
    { _logger = logger; _options = options.Value; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        InitializeChilkat();

        _listener = new TcpListener(IPAddress.Parse(_options.ListenAddress), _options.ListenPort);
        _listener.Start();
        LogRelayListening(_logger, _options.ListenAddress, _options.ListenPort);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { var client = await _listener.AcceptTcpClientAsync(stoppingToken); _ = HandleClientAsync(client, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { LogAcceptConnectionError(_logger, ex); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, true))
            using (var writer = new StreamWriter(stream, Encoding.ASCII, 8192, true) { NewLine = "\r\n", AutoFlush = true })
            {
                await Reply(writer, "220 localhost MailRelay");
                string? from = null; var recipients = new List<string>();
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct); if (line is null) break;
                    if (line.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase))
                    { await Reply(writer, "250-localhost"); await Reply(writer, $"250 SIZE {_options.MaxMessageSizeBytes}"); }
                    else if (line.StartsWith("HELO ", StringComparison.OrdinalIgnoreCase)) await Reply(writer, "250 localhost");
                    else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase))
                    { from = Address(line["MAIL FROM:".Length..]); recipients.Clear(); await Reply(writer, "250 OK"); }
                    else if (line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                    { var to = Address(line["RCPT TO:".Length..]); if (to.Length > 0) recipients.Add(to); await Reply(writer, "250 OK"); }
                    else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(from)) { await Reply(writer, "503 MAIL FROM required"); continue; }
                        if (recipients.Count == 0) { await Reply(writer, "503 RCPT TO required"); continue; }
                        await Reply(writer, "354 End data with <CR><LF>.<CR><LF>");
                        var (mime, tooLarge) = await ReadData(reader, ct);
                        if (tooLarge)
                        {
                            await Reply(writer, "552 Message size exceeds fixed maximum message size");
                        }
                        else
                        {
                            await Reply(
                                writer,
                                await Forward(mime!, from, recipients, ct)
                                    ? "250 Message accepted for delivery"
                                    : "451 Unable to relay message");
                        }
                        from = null; recipients.Clear();
                    }
                    else if (line.Equals("RSET", StringComparison.OrdinalIgnoreCase)) { from = null; recipients.Clear(); await Reply(writer, "250 OK"); }
                    else if (line.Equals("NOOP", StringComparison.OrdinalIgnoreCase)) await Reply(writer, "250 OK");
                    else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase)) { await Reply(writer, "221 Bye"); break; }
                    else await Reply(writer, "502 Command not implemented");
                }
            }
        }
        catch (Exception ex) { LogClientConnectionError(_logger, ex); }
    }

    private async Task<(string? Mime, bool TooLarge)> ReadData(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder(); var size = 0;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct); if (line is null || line == ".") break;
            if (line.StartsWith("..")) line = line[1..];
            size += Encoding.UTF8.GetByteCount(line) + 2;
            if (size > _options.MaxMessageSizeBytes) { await Drain(reader, ct); return (null, true); }
            sb.Append(line).Append("\r\n");
        }
        return (sb.ToString(), false);
    }

    private static async Task Drain(StreamReader reader, CancellationToken ct)
    { while (true) { var line = await reader.ReadLineAsync(ct); if (line is null || line == ".") break; } }

    private async Task<bool> Forward(string mime, string envelopeFrom, IReadOnlyCollection<string> recipients, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                var email = new Chilkat.Email();
                if (!email.SetFromMimeText(mime)) { LogMimeParseError(_logger, email.LastErrorText); return false; }
                if (string.IsNullOrWhiteSpace(email.FromAddress)) email.From = envelopeFrom;
                var mailman = new Chilkat.MailMan { SmtpHost = _options.SmtpHost, SmtpPort = _options.SmtpPort, SmtpSsl = _options.SmtpSsl, SmtpUsername = _options.Username, SmtpPassword = _options.Password };
                LogForwardingMail(
                    _logger,
                    envelopeFrom,
                    recipients);
                if (!mailman.SendEmail(email)) { LogSmtpError(_logger, mailman.LastErrorText); return false; }
                LogMailForwarded(_logger); return true;
            }
            catch (Exception ex) { LogUnexpectedForwardingError(_logger, ex); return false; }
        }, ct);
    }

    private void InitializeChilkat()
    {
        if (string.IsNullOrWhiteSpace(_options.ChilkatLicenseKey))
        {
            throw new InvalidOperationException(
                "Chilkat license key is not configured.");
        }

        var global = new Chilkat.Global();

        if (!global.UnlockBundle(_options.ChilkatLicenseKey))
        {
            throw new InvalidOperationException(
                $"Unable to unlock Chilkat: {global.LastErrorText}");
        }

        LogChilkatInitialized(_logger);
    }


    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "SMTP relay listening on {Address}:{Port}")]
    private static partial void LogRelayListening(
        ILogger logger,
        string address,
        int port);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Forwarding mail from {From} to {Recipients}")]
    private static partial void LogForwardingMail(
        ILogger logger,
        string from,
        IReadOnlyCollection<string> recipients);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Mail successfully forwarded.")]
    private static partial void LogMailForwarded(ILogger logger);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Information,
        Message = "Chilkat successfully initialized.")]
    private static partial void LogChilkatInitialized(ILogger logger);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Error,
        Message = "Error accepting SMTP connection.")]
    private static partial void LogAcceptConnectionError(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "SMTP client connection error.")]
    private static partial void LogClientConnectionError(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Error,
        Message = "Unable to parse MIME: {Error}")]
    private static partial void LogMimeParseError(
        ILogger logger,
        string error);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Error,
        Message = "Chilkat SMTP error: {Error}")]
    private static partial void LogSmtpError(
        ILogger logger,
        string error);

    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Error,
        Message = "Unexpected SMTP forwarding error.")]
    private static partial void LogUnexpectedForwardingError(
        ILogger logger,
        Exception exception);

    private static string Address(string value)
    {
        value = value.Trim(); var s = value.IndexOf('<'); var e = value.IndexOf('>');
        if (s >= 0 && e > s) return value.Substring(s + 1, e - s - 1).Trim();
        var space = value.IndexOf(' '); return (space > 0 ? value[..space] : value).Trim();
    }

    private static Task Reply(StreamWriter writer, string response) => writer.WriteLineAsync(response);
    public override Task StopAsync(CancellationToken cancellationToken) { _listener?.Stop(); return base.StopAsync(cancellationToken); }
}