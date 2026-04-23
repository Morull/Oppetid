using KraftverkUptime.Web;
using KraftverkUptime.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// API-baseadresse konfigureres via wwwroot/appsettings.json (ApiBaseAddress).
var apiBase = builder.Configuration["ApiBaseAddress"] ?? builder.HostEnvironment.BaseAddress;
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBase) });

// Typed API-klient for rapporter og opplasting.
builder.Services.AddScoped<ReportsApi>();

// Brukerkontekst – v1 injiserer en lokal stub; v2 bytter til MSAL-autentisert variant.
builder.Services.AddScoped<IUserContextProvider, AnonymousUserContextProvider>();

await builder.Build().RunAsync();
