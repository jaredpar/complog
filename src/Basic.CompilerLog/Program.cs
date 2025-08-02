
using Basic.CompilerLog.App;

var appDataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Basic.CompilerLog",
    Guid.NewGuid().ToString());
var app = new CompLogApp(
    Environment.CurrentDirectory,
    appDataDirectory,
    Console.Out);
return app.Run(args);