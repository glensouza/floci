using System.Reflection;
using FlociLab.Aws.DynamoDb;
using FlociLab.Aws.Kms;
using FlociLab.Aws.S3;
using FlociLab.Aws.SecretsManager;
using FlociLab.Aws.Sns;
using FlociLab.Aws.Sqs;
using FlociLab.Aws.Web.Components;
using FlociLab.Core;
using FlociLab.Core.Coverage;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Options binding, the four endpoint factories, the demo catalog and the coverage matrix, then
// one .Add<Service>Demo() per AWS sample this host carries — its page, route and nav entry all
// come with it.
builder.Services
    .AddFlociCore(builder.Configuration)
    .AddAwsS3Demo()
    .AddAwsSqsDemo()
    .AddAwsDynamoDbDemo()
    .AddAwsKmsDemo()
    .AddAwsSecretsManagerDemo()
    .AddAwsSnsDemo();

WebApplication app = builder.Build();

// Which assemblies own routable pages is a question only the registration above can answer, so
// ask the catalog rather than repeating it by hand. Routes.razor asks it again for the Router, and
// both get the same answer (docs/BLAZOR-PLAN.md §6).
Assembly[] pageAssemblies;

using (IServiceScope scope = app.Services.CreateScope())
{
    pageAssemblies = [.. scope.ServiceProvider.GetRequiredService<IDemoCatalog>().PageAssemblies];
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
    .AddAdditionalAssemblies(pageAssemblies)
    .AddInteractiveServerRenderMode();

app.Run();
