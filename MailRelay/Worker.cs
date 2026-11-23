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
    private static readonly HttpClient HttpClient = new();

    private readonly SemaphoreSlim _queueSignal = new(0, 1);
    private readonly SemaphoreSlim _oauthTokenLock = new(1, 1);

    private TcpListener? _listener;
    private string _queueDirectory = "";
    private string _failedDirectory = "";
    private string? _oauthAccessToken;
    private DateTime _oauthTokenExpiresUtc;

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
        ValidateLocalAuthenticationOptions();
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
                var authenticated =
                    !_options.LocalAuthentication.Enabled;

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);

                    if (line is null)
                    {
                        break;
                    }

                    LogSmtpCommandReceived(
                        _logger,
                        GetSmtpCommandName(line));

                    if (line.StartsWith(
                            "EHLO ",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        await Reply(writer, "250-localhost");

                        if (_options.LocalAuthentication.Enabled)
                        {
                            await Reply(writer, "250-AUTH LOGIN PLAIN");
                        }

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
                                 "AUTH LOGIN",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        LogLocalAuthMechanismReceived(
                            _logger,
                            "LOGIN");

                        authenticated = await AuthenticateLocalClientAsync(
                            line,
                            reader,
                            writer,
                            ct);

                        if (authenticated)
                        {
                            LogLocalAuthenticationSuccessful(_logger);

                            await Reply(
                                writer,
                                "235 Authentication successful");
                        }
                        else
                        {
                            LogLocalAuthenticationFailed(_logger);

                            await Reply(
                                writer,
                                "535 Authentication failed");
                        }
                    }
                    else if (line.StartsWith(
                                 "AUTH PLAIN",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        LogLocalAuthMechanismReceived(
                            _logger,
                            "PLAIN");

                        authenticated = await AuthenticateLocalClientPlainAsync(
                            line,
                            reader,
                            writer,
                            ct);

                        if (authenticated)
                        {
                            LogLocalAuthenticationSuccessful(_logger);

                            await Reply(
                                writer,
                                "235 Authentication successful");
                        }
                        else
                        {
                            LogLocalAuthenticationFailed(_logger);

                            await Reply(
                                writer,
                                "535 Authentication failed");
                        }
                    }
                    else if (line.StartsWith(
                                 "MAIL FROM:",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        if (!authenticated)
                        {
                            await Reply(
                                writer,
                                "530 Authentication required");
                            continue;
                        }

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

    private async Task<bool> AuthenticateLocalClientAsync(
        string authLine,
        StreamReader reader,
        StreamWriter writer,
        CancellationToken ct)
    {
        string? username;

        var parts = authLine.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 3)
        {
            username = DecodeBase64(parts[2]);
        }
        else
        {
            await Reply(writer, "334 VXNlcm5hbWU6");

            var usernameBase64 =
                await reader.ReadLineAsync(ct);

            username = DecodeBase64(usernameBase64);
        }

        if (username is null)
        {
            return false;
        }

        await Reply(writer, "334 UGFzc3dvcmQ6");

        var passwordBase64 =
            await reader.ReadLineAsync(ct);

        var password =
            DecodeBase64(passwordBase64);

        if (password is null)
        {
            return false;
        }

        return FixedTimeEquals(
                   username,
                   _options.LocalAuthentication.Username) &&
               FixedTimeEquals(
                   password,
                   _options.LocalAuthentication.Password);
    }

    private async Task<bool> AuthenticateLocalClientPlainAsync(
        string authLine,
        StreamReader reader,
        StreamWriter writer,
        CancellationToken ct)
    {
        var parts = authLine.Split(
            ' ',
            3,
            StringSplitOptions.RemoveEmptyEntries);

        string? payloadBase64;

        if (parts.Length >= 3)
        {
            payloadBase64 = parts[2];
        }
        else
        {
            await Reply(writer, "334");

            payloadBase64 =
                await reader.ReadLineAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(payloadBase64))
        {
            return false;
        }

        string payload;

        try
        {
            payload = Encoding.UTF8.GetString(
                Convert.FromBase64String(
                    payloadBase64.Trim()));
        }
        catch (FormatException)
        {
            return false;
        }

        var fields = payload.Split('\0');

        if (fields.Length < 3)
        {
            return false;
        }

        var username = fields[^2];
        var password = fields[^1];

        return FixedTimeEquals(
                   username,
                   _options.LocalAuthentication.Username) &&
               FixedTimeEquals(
                   password,
                   _options.LocalAuthentication.Password);
    }

    private static string? DecodeBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(
                Convert.FromBase64String(value.Trim()));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool FixedTimeEquals(
        string left,
        string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);

        if (leftBytes.Length != rightBytes.Length)
        {
            return false;
        }

        return System.Security.Cryptography.CryptographicOperations
            .FixedTimeEquals(leftBytes, rightBytes);
    }

    private void ValidateLocalAuthenticationOptions()
    {
        if (!_options.LocalAuthentication.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(
                _options.LocalAuthentication.Username))
        {
            throw new InvalidOperationException(
                "Relay:LocalAuthentication:Username is required when local authentication is enabled.");
        }

        if (string.IsNullOrWhiteSpace(
                _options.LocalAuthentication.Password))
        {
            throw new InvalidOperationException(
                "Relay:LocalAuthentication:Password is required when local authentication is enabled.");
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
        string? oauthAccessToken = null;

        try
        {
            if (UsesOAuth2())
            {
                oauthAccessToken =
                    await GetOAuthAccessTokenAsync(ct);
            }
            else if (!UsesPasswordAuthentication())
            {
                throw new InvalidOperationException(
                    $"Unsupported AuthenticationMode '{_options.AuthenticationMode}'. " +
                    "Use 'Password' or 'OAuth2'.");
            }
        }
        catch (Exception ex)
        {
            LogOAuthTokenError(_logger, ex);
            return false;
        }

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
                        StartTLS = _options.SmtpStartTls,
                        SmtpUsername = _options.Username,
                        SmtpPassword = UsesOAuth2()
                            ? ""
                            : _options.Password
                    };

                    if (oauthAccessToken is not null)
                    {
                        mailman.OAuth2AccessToken =
                            oauthAccessToken;
                    }

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

    private bool UsesOAuth2()
        => string.Equals(
            _options.AuthenticationMode,
            "OAuth2",
            StringComparison.OrdinalIgnoreCase);

    private bool UsesPasswordAuthentication()
        => string.Equals(
            _options.AuthenticationMode,
            "Password",
            StringComparison.OrdinalIgnoreCase);

    private async Task<string> GetOAuthAccessTokenAsync(
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_oauthAccessToken) &&
            _oauthTokenExpiresUtc >
                DateTime.UtcNow.AddMinutes(5))
        {
            return _oauthAccessToken;
        }

        await _oauthTokenLock.WaitAsync(ct);

        try
        {
            if (!string.IsNullOrWhiteSpace(_oauthAccessToken) &&
                _oauthTokenExpiresUtc >
                    DateTime.UtcNow.AddMinutes(5))
            {
                return _oauthAccessToken;
            }

            ValidateOAuthOptions();

            using var content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["client_id"] = _options.OAuth.ClientId,
                    ["client_secret"] = _options.OAuth.ClientSecret,
                    ["scope"] = _options.OAuth.Scope,
                    ["grant_type"] = "client_credentials"
                });

            using var response = await HttpClient.PostAsync(
                $"https://login.microsoftonline.com/{_options.OAuth.TenantId}/oauth2/v2.0/token",
                content,
                ct);

            var responseBody =
                await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"OAuth token request failed with HTTP {(int)response.StatusCode}.");
            }

            using var json = JsonDocument.Parse(responseBody);

            var root = json.RootElement;

            if (!root.TryGetProperty(
                    "access_token",
                    out var tokenElement))
            {
                throw new InvalidOperationException(
                    "OAuth token response does not contain access_token.");
            }

            _oauthAccessToken =
                tokenElement.GetString()
                ?? throw new InvalidOperationException(
                    "OAuth access_token is empty.");

            var expiresIn =
                root.TryGetProperty(
                    "expires_in",
                    out var expiresElement) &&
                expiresElement.TryGetInt32(out var seconds)
                    ? seconds
                    : 3600;

            _oauthTokenExpiresUtc =
                DateTime.UtcNow.AddSeconds(
                    Math.Max(60, expiresIn));

            LogOAuthTokenAcquired(
                _logger,
                _oauthTokenExpiresUtc);

            return _oauthAccessToken;
        }
        finally
        {
            _oauthTokenLock.Release();
        }
    }

    private void ValidateOAuthOptions()
    {
        if (string.IsNullOrWhiteSpace(_options.Username))
        {
            throw new InvalidOperationException(
                "Relay:Username is required for OAuth2 SMTP.");
        }

        if (string.IsNullOrWhiteSpace(_options.OAuth.TenantId))
        {
            throw new InvalidOperationException(
                "Relay:OAuth:TenantId is required.");
        }

        if (string.IsNullOrWhiteSpace(_options.OAuth.ClientId))
        {
            throw new InvalidOperationException(
                "Relay:OAuth:ClientId is required.");
        }

        if (string.IsNullOrWhiteSpace(_options.OAuth.ClientSecret))
        {
            throw new InvalidOperationException(
                "Relay:OAuth:ClientSecret is required.");
        }

        if (string.IsNullOrWhiteSpace(_options.OAuth.Scope))
        {
            throw new InvalidOperationException(
                "Relay:OAuth:Scope is required.");
        }
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

    private static string GetSmtpCommandName(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "(empty)";
        }

        var separator = line.IndexOf(' ');

        return (separator > 0
                ? line[..separator]
                : line)
            .Trim()
            .ToUpperInvariant();
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
        EventId = 1010,
        Level = LogLevel.Information,
        Message = "OAuth2 access token acquired; expires at {ExpiresUtc:u}.")]
    private static partial void LogOAuthTokenAcquired(
        ILogger logger,
        DateTime expiresUtc);

    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Information,
        Message = "Local SMTP AUTH {Mechanism} received.")]
    private static partial void LogLocalAuthMechanismReceived(
        ILogger logger,
        string mechanism);

    [LoggerMessage(
        EventId = 1012,
        Level = LogLevel.Information,
        Message = "Local SMTP authentication successful.")]
    private static partial void LogLocalAuthenticationSuccessful(
        ILogger logger);

    [LoggerMessage(
        EventId = 1013,
        Level = LogLevel.Information,
        Message = "SMTP command received: {Command}")]
    private static partial void LogSmtpCommandReceived(
        ILogger logger,
        string command);

    [LoggerMessage(
        EventId = 2012,
        Level = LogLevel.Warning,
        Message = "Local SMTP authentication failed.")]
    private static partial void LogLocalAuthenticationFailed(
        ILogger logger);

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

    [LoggerMessage(
        EventId = 2011,
        Level = LogLevel.Error,
        Message = "Unable to acquire OAuth2 access token.")]
    private static partial void LogOAuthTokenError(
        ILogger logger,
        Exception exception);
}
