using MailRelay;

var builder = Host.CreateApplicationBuilder(args);

// Optional local overrides for development.
// appsettings.Local.json is ignored by Git and can contain local secrets.
builder.Configuration.AddJsonFile(
    "appsettings.Local.json",
    optional: true,
    reloadOnChange: false);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Mail Relay";
});

builder.Services.Configure<RelayOptions>(
    builder.Configuration.GetSection("Relay"));

builder.Services.AddHostedService<Worker>();

builder.Build().Run();
