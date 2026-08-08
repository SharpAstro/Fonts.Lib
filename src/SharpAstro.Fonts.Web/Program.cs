using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SharpAstro.Fonts.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// No Router: this is a single page, so the component mounts straight onto #app and the whole
// Router/RouteView/NotFound layer never enters the bundle.
builder.RootComponents.Add<Compare>("#app");

// Fonts are ordinary same-origin static assets under wwwroot/fonts/, fetched on demand rather
// than embedded, so a visitor only pays for the faces they actually open.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

await builder.Build().RunAsync();
