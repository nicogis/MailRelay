using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace MailRelay;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly RelayOptions _options;
    private TcpListener? _listener;

    public Worker(ILogger<Worker> logger, IOptions<RelayOptions> options)
    { _logger = logger; _options = options.Value; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new TcpListener(IPAddress.Parse(_options.ListenAddress), _options.ListenPort);
        _listener.Start();
        _logger.LogInformation("SMTP relay listening on {Address}:{Port}", _options.ListenAddress, _options.ListenPort);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { var client = await _listener.AcceptTcpClientAsync(stoppingToken); _ = HandleClientAsync(client, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Error accepting SMTP connection."); }
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
                        var data = await ReadData(reader, ct);
                        if (data.TooLarge) await Reply(writer, "552 Message size exceeds fixed maximum message size");
                        else await Reply(writer, await Forward(data.Mime!, from, recipients, ct) ? "250 Message accepted for delivery" : "451 Unable to relay message");
                        from = null; recipients.Clear();
                    }
                    else if (line.Equals("RSET", StringComparison.OrdinalIgnoreCase)) { from = null; recipients.Clear(); await Reply(writer, "250 OK"); }
                    else if (line.Equals("NOOP", StringComparison.OrdinalIgnoreCase)) await Reply(writer, "250 OK");
                    else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase)) { await Reply(writer, "221 Bye"); break; }
                    else await Reply(writer, "502 Command not implemented");
                }
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "SMTP client connection error."); }
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
                if (!string.IsNullOrWhiteSpace(_options.ChilkatLicenseKey))
                {
                    var global = new Chilkat.Global();
                    if (!global.UnlockBundle(_options.ChilkatLicenseKey)) { _logger.LogError("Chilkat license error: {Error}", global.LastErrorText); return false; }
                }
                var email = new Chilkat.Email();
                if (!email.SetFromMimeText(mime)) { _logger.LogError("Unable to parse MIME: {Error}", email.LastErrorText); return false; }
                if (string.IsNullOrWhiteSpace(email.FromAddress)) email.From = envelopeFrom;
                var mailman = new Chilkat.MailMan { SmtpHost = _options.SmtpHost, SmtpPort = _options.SmtpPort, SmtpSsl = _options.SmtpSsl, SmtpUsername = _options.Username, SmtpPassword = _options.Password };
                _logger.LogInformation("Forwarding mail from {From} to {Recipients}", envelopeFrom, string.Join(", ", recipients));
                if (!mailman.SendEmail(email)) { _logger.LogError("Chilkat SMTP error: {Error}", mailman.LastErrorText); return false; }
                _logger.LogInformation("Mail successfully forwarded."); return true;
            }
            catch (Exception ex) { _logger.LogError(ex, "Unexpected SMTP forwarding error."); return false; }
        }, ct);
    }

    private static string Address(string value)
    {
        value = value.Trim(); var s = value.IndexOf('<'); var e = value.IndexOf('>');
        if (s >= 0 && e > s) return value.Substring(s + 1, e - s - 1).Trim();
        var space = value.IndexOf(' '); return (space > 0 ? value[..space] : value).Trim();
    }

    private static Task Reply(StreamWriter writer, string response) => writer.WriteLineAsync(response);
    public override Task StopAsync(CancellationToken cancellationToken) { _listener?.Stop(); return base.StopAsync(cancellationToken); }
}