using System.Reflection;
using FlociLab.All.Web.Components;
using FlociLab.Aws.S3;
using FlociLab.Azure.Blob;
using FlociLab.Core;
using FlociLab.Core.Coverage;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Options binding, the four endpoint factories, the demo catalog and the coverage matrix, then
// one .Add<Service>Demo() per sample RCL — each brings its own page, route and nav entry with it.
builder.Services
    .AddFlociCore(builder.Configuration)
    .AddAwsS3Demo()
    .AddAzureBlobDemo();

WebApplication app = builder.Build();

// Which assemblies own sample pages is a question only the registrations above can answer, so ask
// the catalog rather than repeating the list. Routes.razor asks it again for the Router.
Assembly[] sampleAssemblies;

using (IServiceScope scope = app.Services.CreateScope())
{
    sampleAssemblies = scope.ServiceProvider.GetRequiredService<IDemoCatalog>().SampleAssemblies();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddAdditionalAssemblies(sampleAssemblies)
    .AddInteractiveServerRenderMode();

app.Run();
