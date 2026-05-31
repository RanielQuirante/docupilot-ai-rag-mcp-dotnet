namespace DocuPilot.Models.Enums;

/// <summary>
/// The minimal closed lifecycle set for a workflow task (spec §11.7 — the Open/Completed list +
/// Complete button; PM Q6). Persisted as the enum <b>name</b> string in
/// <c>WorkflowTasks.Status NVARCHAR(50)</c> via <c>.HasConversion&lt;string&gt;()</c> (DBA DA-053
/// §P8.5.2). A task is created <see cref="Open"/>; completing it flips it to <see cref="Completed"/>
/// (and sets <c>CompletedAt</c>).
/// </summary>
public enum WorkflowTaskStatus
{
    /// <summary>Created and awaiting action. The value every new task carries.</summary>
    Open,

    /// <summary>Marked done — <c>CompletedAt</c> is set. Terminal.</summary>
    Completed,
}
