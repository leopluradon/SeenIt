using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SeenIt.UI;
using SeenIt.UI.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
// AuthorizeView logs every "not signed in" check at Information; keep the console quiet.
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// The website is served by the API server, so the API is on the same origin.
builder.Services.AddSeenItUi(new Uri(builder.HostEnvironment.BaseAddress));

await builder.Build().RunAsync();
