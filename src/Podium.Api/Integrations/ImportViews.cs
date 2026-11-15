namespace Podium.Api.Integrations;

public sealed record SpaceView(string Key, string Name, string? Description, bool Imported, Guid? ProjectId);

public sealed record SprintChoice(string Id, string Name, string State, string? Goal, DateOnly? Start, DateOnly? End, bool Imported, Guid? SprintId);

public sealed record ImportSpacesRequest(List<string>? Keys);

public sealed record ImportSprintsRequest(List<string>? SprintIds);

public sealed record PersonView(Guid Id, string DisplayName, string? Email, Guid? UserId, string? UserName, bool Invited, bool Active);

/// <summary>An admin's correction of a person: the e-mail, the member it stands for, or <c>Unlink</c> to undo that link (F-068).</summary>
public sealed record PersonInput(string? Email, Guid? UserId, bool? Unlink = null);

public sealed record SyncRunView(Guid Id, string Kind, string Status, int Created, int Updated, int Archived, int People, string? Error, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);
