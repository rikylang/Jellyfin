using LiveHub.MockServer;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
MockServerApp.Map(app, MockOptions.FromEnvironment());
app.Run();
