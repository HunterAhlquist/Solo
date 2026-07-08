namespace Solo.Shared;

/// <summary>Project membership role. The whole access model — deliberately simple.</summary>
public enum ProjectRole
{
    Read = 0,
    ReadWrite = 1,
}

/// <summary>
/// Task status; drives placement in the three fixed Kanban columns
/// (Unstarted / In Progress / Finished).
/// </summary>
public enum TaskState
{
    Unstarted = 0,
    InProgress = 1,
    Finished = 2,
}

/// <summary>Which entity kind a label applies to.</summary>
public enum LabelKind
{
    Task = 0,
    SubTask = 1,
}

/// <summary>Presence state a client reports for an entity.</summary>
public enum PresenceMode
{
    None = 0,
    Viewing = 1,
    Editing = 2,
}

/// <summary>What happened to an entity in a change broadcast.</summary>
public enum ChangeKind
{
    Created = 0,
    Updated = 1,
    Deleted = 2,
}
