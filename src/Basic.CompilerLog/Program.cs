
var appDataDirectory = Path.Combine(Constants.LocalAppDataDirectory, Guid.NewGuid().ToString());
try
{
    var app = new CompLogApp(appDataDirectory);
    return app.Run(args);
}
finally
{
    if (Directory.Exists(appDataDirectory))
    {
        Directory.Delete(appDataDirectory, recursive: true);
    }
}