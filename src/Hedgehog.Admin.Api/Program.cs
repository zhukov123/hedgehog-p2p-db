using Hedgehog.Admin.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowAnyOrigin();
    });
});
builder.Services.AddSingleton<AdminRepository>();

var app = builder.Build();

app.UseCors();

var admin = app.MapGroup("/admin/v1")
    .WithTags("Hedgehog Admin v1");

var adminCompat = app.MapGroup("/admin")
    .WithTags("Hedgehog Admin compatibility");

admin.MapGet("/", () => Results.Ok(new
{
    service = "Hedgehog.Admin.Api",
    version = "v1",
    surfaces = new[]
    {
        "/cluster/status",
        "/nodes",
        "/capacity",
        "/objects",
        "/repair/queue",
        "/audit/events",
        "/recovery/gates",
    },
}));

admin.MapGet("/cluster/status", (AdminRepository repository) =>
    Results.Ok(repository.GetClusterStatus()));
admin.MapGet("/status", (AdminRepository repository) =>
    Results.Ok(repository.GetClusterStatus()));
adminCompat.MapGet("/status", (AdminRepository repository) =>
    Results.Ok(repository.GetClusterStatus()));

admin.MapPost("/cluster/actions/{action}", (
    string action,
    ActionRequestDto request,
    AdminRepository repository) =>
    ApplyGuardedAction(repository, "cluster", "cluster", action, request));

admin.MapGet("/nodes", (AdminRepository repository) =>
    Results.Ok(repository.GetNodes()));
adminCompat.MapGet("/nodes", (AdminRepository repository) =>
    Results.Ok(repository.GetNodes()));

admin.MapGet("/nodes/{nodeId}", (string nodeId, AdminRepository repository) =>
{
    var node = repository.GetNode(nodeId);
    return node is null ? Results.NotFound() : Results.Ok(node);
});

admin.MapPost("/nodes/{nodeId}/actions/{action}", (
    string nodeId,
    string action,
    ActionRequestDto request,
    AdminRepository repository) =>
    ApplyGuardedAction(repository, "node", nodeId, action, request));
adminCompat.MapPost("/nodes/{nodeId}/actions/{action}", (
    string nodeId,
    string action,
    ActionRequestDto request,
    AdminRepository repository) =>
    ApplyGuardedAction(repository, "node", nodeId, action, request));

admin.MapGet("/capacity", (AdminRepository repository) =>
    Results.Ok(repository.GetCapacity()));
adminCompat.MapGet("/capacity", (AdminRepository repository) =>
    Results.Ok(repository.GetCapacity()));

admin.MapPost("/capacity/scopes/{scopeId}/actions/{action}", (
    string scopeId,
    string action,
    ActionRequestDto request,
    AdminRepository repository) =>
    ApplyGuardedAction(repository, "capacity", scopeId, action, request));

admin.MapGet("/objects", (
    string? tenantId,
    string? datasetId,
    string? state,
    string? q,
    AdminRepository repository) =>
    Results.Ok(repository.GetObjects(tenantId, datasetId, state, q)));
adminCompat.MapGet("/objects", (
    string? tenantId,
    string? datasetId,
    string? state,
    string? q,
    AdminRepository repository) =>
    Results.Ok(repository.GetObjects(tenantId, datasetId, state, q)));

admin.MapGet("/objects/{objectId}", (string objectId, AdminRepository repository) =>
{
    var objectVersion = repository.GetObject(objectId);
    return objectVersion is null ? Results.NotFound() : Results.Ok(objectVersion);
});
adminCompat.MapGet("/objects/{objectId}", (string objectId, AdminRepository repository) =>
{
    var objectVersion = repository.GetObject(objectId);
    return objectVersion is null ? Results.NotFound() : Results.Ok(objectVersion);
});

admin.MapPost("/objects/{versionId}/actions/{action}", (
    string versionId,
    string action,
    ActionRequestDto request,
    AdminRepository repository) =>
    ApplyGuardedAction(repository, "object", versionId, action, request));

admin.MapGet("/repair/queue", (
    string? state,
    string? priority,
    AdminRepository repository) =>
    Results.Ok(repository.GetRepairQueue(state, priority)));
admin.MapGet("/repair/jobs", (
    string? state,
    string? priority,
    AdminRepository repository) =>
    Results.Ok(repository.GetRepairQueue(state, priority)));
adminCompat.MapGet("/repair/jobs", (
    string? state,
    string? priority,
    AdminRepository repository) =>
    Results.Ok(repository.GetRepairQueue(state, priority)));

admin.MapPost("/repair/jobs/{jobId}/actions/{action}", (
    string jobId,
    string action,
    ActionRequestDto request,
    AdminRepository repository) =>
    ApplyGuardedAction(repository, "repair-job", jobId, action, request));
adminCompat.MapPost("/repair/jobs/{jobId}/actions/{action}", (
    string jobId,
    string action,
    ActionRequestDto request,
    AdminRepository repository) =>
    ApplyGuardedAction(repository, "repair-job", jobId, action, request));

admin.MapPost("/repair/classes/{repairClass}/actions/{action}", (
    string repairClass,
    string action,
    ActionRequestDto request,
    AdminRepository repository) =>
    ApplyGuardedAction(repository, "repair-class", repairClass, action, request));

admin.MapGet("/audit/events", (
    string? actorId,
    string? action,
    string? targetType,
    string? result,
    AdminRepository repository) =>
    Results.Ok(repository.GetAuditEvents(actorId, action, targetType, result)));
admin.MapGet("/audit", (
    string? actorId,
    string? action,
    string? targetType,
    string? result,
    AdminRepository repository) =>
    Results.Ok(repository.GetAuditEvents(actorId, action, targetType, result)));
adminCompat.MapGet("/audit", (
    string? actorId,
    string? action,
    string? targetType,
    string? result,
    AdminRepository repository) =>
    Results.Ok(repository.GetAuditEvents(actorId, action, targetType, result)));

admin.MapGet("/recovery/gates", (AdminRepository repository) =>
    Results.Ok(repository.GetRecoveryGates()));
adminCompat.MapGet("/recovery/gates", (AdminRepository repository) =>
    Results.Ok(repository.GetRecoveryGates()));

admin.MapPost("/recovery/gates/{gateId}/actions/{action}", (
    string gateId,
    string action,
    ActionRequestDto request,
    AdminRepository repository) =>
    ApplyGuardedAction(repository, "recovery-gate", gateId, action, request));
adminCompat.MapPost("/recovery/gates/{gateId}/actions/{action}", (
    string gateId,
    string action,
    ActionRequestDto request,
    AdminRepository repository) =>
    ApplyGuardedAction(repository, "recovery-gate", gateId, action, request));

app.MapGet("/", () => Results.Redirect("/admin/v1"));

app.Run();

static IResult ApplyGuardedAction(
    AdminRepository repository,
    string targetType,
    string targetId,
    string action,
    ActionRequestDto request)
{
    if (string.IsNullOrWhiteSpace(request.ActorId) || string.IsNullOrWhiteSpace(request.Reason))
    {
        return Results.BadRequest(new
        {
            error = "admin action requires actorId and reason",
        });
    }

    return Results.Ok(repository.ApplyAction(targetType, targetId, action, request));
}
