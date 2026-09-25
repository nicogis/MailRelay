using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MailRelay;

public sealed partial class Worker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly ILogger<Worker> _logger;
    private readonly RelayOptions _options;
    private readonly SemaphoreSlim _queueSignal = new(0, 1);

    private TcpListener? _listener;
    private string _queueDirectory = "";
    private string _failedDirectory = "";

    public Worker(
        ILogger<Worker> logger,
        IOptions<RelayOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        InitializeChilkat();
        InitializeQueue();

        _listener = new TcpListener(
            IPAddress.Parse(_options.ListenAddress),
            _options.ListenPort);

        _listener.Start();

        LogRelayListening(
            _logger,
            _options.ListenAddress,
            _options.ListenPort);

        await Task.WhenAll(
            ListenLoopAsync(stoppingToken),
            QueueLoopAsync(stoppingToken));
    }

    private async Task ListenLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client =
                    await _listener!.AcceptTcpClientAsync(stoppingToken);

                _ = HandleClientAsync(client, stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogAcceptConnectionError(_logger, ex);
            }
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken ct)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(
                       stream,
                       Encoding.ASCII,
                       false,
                       8192,
                       true))
            using (var writer = new StreamWriter(
                       stream,
                       Encoding.ASCII,
                       8192,
                       true)
                   {
                       NewLine = "\r\n",
                       AutoFlush = true
                   })
            {
                await Reply(writer, "220 localhost MailRelay");

                string? from = null;
                var recipients = new List<string>();

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);

                    if (line is null)
                    {
                        break;
                    }

                    if (line.StartsWith(
                            "EHLO ",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        await Reply(writer, "250-localhost");
                        await Reply(
                            writer,
                            $"250 SIZE {_options.MaxMessageSizeBytes}");
                    }
                    else if (line.StartsWith(
                                 "HELO ",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        await Reply(writer, "250 localhost");
                    }
                    else if (line.StartsWith(
                                 "MAIL FROM:",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        from = Address(line["MAIL FROM:".Length..]);
                        recipients.Clear();
                        await Reply(writer, "250 OK");
                    }
                    else if (line.StartsWith(
                                 "RCPT TO:",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        var to = Address(line["RCPT TO:".Length..]);

                        if (to.Length > 0)
                        {
                            recipients.Add(to);
                        }

                        await Reply(writer, "250 OK");
                    }
                    else if (line.Equals(
                                 "DATA",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(from))
                        {
                            await Reply(writer, "503 MAIL FROM required");
                            continue;
                        }

                        if (recipients.Count == 0)
                        {
                            await Reply(writer, "503 RCPT TO required");
                            continue;
                        }

                        await Reply(
                            writer,
                            "354 End data with <CR><LF>.<CR><LF>");

                        var (mime, tooLarge) =
                            await ReadData(reader, ct);

                        if (tooLarge)
                        {
                            await Reply(
                                writer,
                                "552 Message size exceeds fixed maximum message size");
                        }
                        else
                        {
                            var queued = await QueueMessageAsync(
                                mime!,
                                from,
                                recipients,
                                ct);

                            await Reply(
                                writer,
                                queued
                                    ? "250 Message accepted for delivery"
                                    : "451 Unable to queue message");
                        }

                        from = null;
                        recipients.Clear();
                    }
                    else if (line.Equals(
                                 "RSET",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        from = null;
                        recipients.Clear();
                        await Reply(writer, "250 OK");
                    }
                    else if (line.Equals(
                                 "NOOP",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        await Reply(writer, "250 OK");
                    }
                    else if (line.Equals(
                                 "QUIT",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        await Reply(writer, "221 Bye");
                        break;
                    }
                    else
                    {
                        await Reply(
                            writer,
                            "502 Command not implemented");
                    }
                }
            }
        }
        catch (IOException ex)
            when (ex.InnerException is SocketException socketException &&
                  socketException.SocketErrorCode is
                      SocketError.ConnectionAborted or
                      SocketError.ConnectionReset)
        {
            LogClientDisconnected(_logger, socketException.SocketErrorCode);
        }
        catch (Exception ex)
        {
            LogClientConnectionError(_logger, ex);
        }
    }

    private async Task<(string? Mime, bool TooLarge)> ReadData(
        StreamReader reader,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        var size = 0;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);

            if (line is null || line == ".")
            {
                break;
            }

            if (line.StartsWith(".."))
            {
                line = line[1..];
            }

            size += Encoding.UTF8.GetByteCount(line) + 2;

            if (size > _options.MaxMessageSizeBytes)
            {
                await Drain(reader, ct);
                return (null, true);
            }

            sb.Append(line).Append("\r\n");
        }

        return (sb.ToString(), false);
    }

    private static async Task Drain(
        StreamReader reader,
        CancellationToken ct)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);

            if (line is null || line == ".")
            {
                break;
            }
        }
    }

    private void InitializeQueue()
    {
        _queueDirectory =
            string.IsNullOrWhiteSpace(_options.QueueDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "MailRelay",
                    "Queue")
                : _options.QueueDirectory;

        _failedDirectory = Path.Combine(
            _queueDirectory,
            "Failed");

        Directory.CreateDirectory(_queueDirectory);
        Directory.CreateDirectory(_failedDirectory);

        LogQueueInitialized(
            _logger,
            _queueDirectory);
    }

    private async Task<bool> QueueMessageAsync(
        string mime,
        string envelopeFrom,
        IReadOnlyCollection<string> recipients,
        CancellationToken ct)
    {
        try
        {
            var id =
                $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";

            var item = new QueuedMail
            {
                Id = id,
                CreatedUtc = DateTime.UtcNow,
                EnvelopeFrom = envelopeFrom,
                Recipients = recipients.ToList(),
                Attempts = 0,
                NextAttemptUtc = DateTime.UtcNow
            };

            var emlPath = GetEmlPath(id);
            var jsonPath = GetJsonPath(id);
            var emlTemp = emlPath + ".tmp";
            var jsonTemp = jsonPath + ".tmp";

            await File.WriteAllTextAsync(
                emlTemp,
                mime,
                Encoding.UTF8,
                ct);

            await File.WriteAllTextAsync(
                jsonTemp,
                JsonSerializer.Serialize(item, JsonOptions),
                Encoding.UTF8,
                ct);

            File.Move(emlTemp, emlPath);
            File.Move(jsonTemp, jsonPath);

            LogMessageQueued(_logger, id);

            if (_queueSignal.CurrentCount == 0)
            {
                _queueSignal.Release();
            }

            return true;
        }
        catch (Exception ex)
        {
            LogQueueWriteError(_logger, ex);
            return false;
        }
    }

    private async Task QueueLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessQueueAsync(ct);

                await _queueSignal.WaitAsync(
                    TimeSpan.FromSeconds(
                        Math.Max(1, _options.RetryBaseSeconds)),
                    ct);
            }
            catch (OperationCanceledException)
                when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogQueueProcessorError(_logger, ex);

                await Task.Delay(
                    TimeSpan.FromSeconds(5),
                    ct);
            }
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        foreach (var jsonPath in Directory.EnumerateFiles(
                     _queueDirectory,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();

            QueuedMail? item;

            try
            {
                var json = await File.ReadAllTextAsync(
                    jsonPath,
                    ct);

                item = JsonSerializer.Deserialize<QueuedMail>(
                    json,
                    JsonOptions);
            }
            catch (Exception ex)
            {
                LogQueueItemReadError(
                    _logger,
                    Path.GetFileName(jsonPath),
                    ex);

                continue;
            }

            if (item is null ||
                item.NextAttemptUtc > DateTime.UtcNow)
            {
                continue;
            }

            var emlPath = GetEmlPath(item.Id);

            if (!File.Exists(emlPath))
            {
                LogQueueMimeMissing(_logger, item.Id);
                continue;
            }

            var mime = await File.ReadAllTextAsync(
                emlPath,
                Encoding.UTF8,
                ct);

            var success = await Forward(
                mime,
                item.EnvelopeFrom,
                item.Recipients,
                ct);

            if (success)
            {
                DeleteQueueItem(item.Id);
                LogQueuedMessageDelivered(
                    _logger,
                    item.Id);

                continue;
            }

            item.Attempts++;
            item.LastError = "SMTP delivery failed.";

            if (_options.MaxRetryAttempts > 0 &&
                item.Attempts >= _options.MaxRetryAttempts)
            {
                MoveToFailed(item.Id);
                LogMessageMovedToFailed(
                    _logger,
                    item.Id,
                    item.Attempts);

                continue;
            }

            var delaySeconds = GetRetryDelaySeconds(
                item.Attempts);

            item.NextAttemptUtc =
                DateTime.UtcNow.AddSeconds(delaySeconds);

            await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(item, JsonOptions),
                Encoding.UTF8,
                ct);

            LogRetryScheduled(
                _logger,
                item.Id,
                item.Attempts,
                delaySeconds);
        }
    }

    private int GetRetryDelaySeconds(int attempts)
    {
        var baseSeconds =
            Math.Max(1, _options.RetryBaseSeconds);

        var maxSeconds =
            Math.Max(baseSeconds, _options.RetryMaxSeconds);

        var exponent =
            Math.Min(Math.Max(0, attempts - 1), 20);

        var delay =
            (long)baseSeconds << exponent;

        return (int)Math.Min(delay, maxSeconds);
    }

    private void DeleteQueueItem(string id)
    {
        TryDelete(GetEmlPath(id));
        TryDelete(GetJsonPath(id));
    }

    private void MoveToFailed(string id)
    {
        MoveIfExists(
            GetEmlPath(id),
            Path.Combine(
                _failedDirectory,
                $"{id}.eml"));

        MoveIfExists(
            GetJsonPath(id),
            Path.Combine(
                _failedDirectory,
                $"{id}.json"));
    }

    private string GetEmlPath(string id)
        => Path.Combine(
            _queueDirectory,
            $"{id}.eml");

    private string GetJsonPath(string id)
        => Path.Combine(
            _queueDirectory,
            $"{id}.json");

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void MoveIfExists(
        string source,
        string destination)
    {
        if (File.Exists(source))
        {
            File.Move(
                source,
                destination,
                true);
        }
    }

    private async Task<bool> Forward(
        string mime,
        string envelopeFrom,
        IReadOnlyCollection<string> recipients,
        CancellationToken ct)
    {
        return await Task.Run(
            () =>
            {
                try
                {
                    var email = new Chilkat.Email();

                    if (!email.SetFromMimeText(mime))
                    {
                        LogMimeParseError(
                            _logger,
                            email.LastErrorText);

                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(
                            email.FromAddress))
                    {
                        email.From = envelopeFrom;
                    }

                    var mailman = new Chilkat.MailMan
                    {
                        SmtpHost = _options.SmtpHost,
                        SmtpPort = _options.SmtpPort,
                        SmtpSsl = _options.SmtpSsl,
                        SmtpUsername = _options.Username,
                        SmtpPassword = _options.Password
                    };

                    LogForwardingMail(
                        _logger,
                        envelopeFrom,
                        recipients);

                    if (!mailman.SendEmail(email))
                    {
                        LogSmtpError(
                            _logger,
                            mailman.LastErrorText);

                        return false;
                    }

                    LogMailForwarded(_logger);
                    return true;
                }
                catch (Exception ex)
                {
                    LogUnexpectedForwardingError(
                        _logger,
                        ex);

                    return false;
                }
            },
            ct);
    }

    private void InitializeChilkat()
    {
        if (string.IsNullOrWhiteSpace(
                _options.ChilkatLicenseKey))
        {
            throw new InvalidOperationException(
                "Chilkat license key is not configured.");
        }

        var global = new Chilkat.Global();

        if (!global.UnlockBundle(
                _options.ChilkatLicenseKey))
        {
            throw new InvalidOperationException(
                $"Unable to unlock Chilkat: {global.LastErrorText}");
        }

        LogChilkatInitialized(_logger);
    }

    private static string Address(string value)
    {
        value = value.Trim();

        var start = value.IndexOf('<');
        var end = value.IndexOf('>');

        if (start >= 0 && end > start)
        {
            return value
                .Substring(
                    start + 1,
                    end - start - 1)
                .Trim();
        }

        var space = value.IndexOf(' ');

        return (space > 0
                ? value[..space]
                : value)
            .Trim();
    }

    private static Task Reply(
        StreamWriter writer,
        string response)
        => writer.WriteLineAsync(response);

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
    private static partial void LogMailForwarded(
        ILogger logger);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Information,
        Message = "Chilkat successfully initialized.")]
    private static partial void LogChilkatInitialized(
        ILogger logger);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Information,
        Message = "Mail queue initialized at {QueueDirectory}")]
    private static partial void LogQueueInitialized(
        ILogger logger,
        string queueDirectory);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Information,
        Message = "Message {MessageId} saved to local queue.")]
    private static partial void LogMessageQueued(
        ILogger logger,
        string messageId);

    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Information,
        Message = "Queued message {MessageId} delivered successfully.")]
    private static partial void LogQueuedMessageDelivered(
        ILogger logger,
        string messageId);

    [LoggerMessage(
        EventId = 1008,
        Level = LogLevel.Information,
        Message = "Retry scheduled for message {MessageId}. Attempt {Attempt}; retry in {DelaySeconds} seconds.")]
    private static partial void LogRetryScheduled(
        ILogger logger,
        string messageId,
        int attempt,
        int delaySeconds);

    [LoggerMessage(
        EventId = 1009,
        Level = LogLevel.Debug,
        Message = "SMTP client disconnected with socket error {SocketErrorCode}.")]
    private static partial void LogClientDisconnected(
        ILogger logger,
        SocketError socketErrorCode);

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

    [LoggerMessage(
        EventId = 2006,
        Level = LogLevel.Error,
        Message = "Unable to save message to local queue.")]
    private static partial void LogQueueWriteError(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 2007,
        Level = LogLevel.Error,
        Message = "Unexpected queue processor error.")]
    private static partial void LogQueueProcessorError(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 2008,
        Level = LogLevel.Error,
        Message = "Unable to read queue item {QueueFile}.")]
    private static partial void LogQueueItemReadError(
        ILogger logger,
        string queueFile,
        Exception exception);

    [LoggerMessage(
        EventId = 2009,
        Level = LogLevel.Error,
        Message = "MIME file is missing for queued message {MessageId}.")]
    private static partial void LogQueueMimeMissing(
        ILogger logger,
        string messageId);

    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Error,
        Message = "Message {MessageId} moved to Failed after {Attempts} delivery attempts.")]
    private static partial void LogMessageMovedToFailed(
        ILogger logger,
        string messageId,
        int attempts);
}
