namespace TurboDL.Core.Model;

/// <summary>Where a batch is in its life.</summary>
public enum BatchStatus
{
    /// <summary>Parts are still downloading or waiting.</summary>
    Downloading,
    /// <summary>7-Zip is extracting the archive sets.</summary>
    Extracting,
    /// <summary>Every archive set was extracted.</summary>
    Extracted,
    /// <summary>All parts finished and there was nothing to extract (or extracting was turned off).</summary>
    Done,
    /// <summary>Stuck until the user acts; <see cref="BatchInfo.Message"/> says why.</summary>
    NeedsAttention,
}

/// <summary>A group of downloads added together from one paste. Stored as JSON in the batches table.</summary>
public sealed class BatchInfo
{
    /// <summary>The database id, set when the batch is added.</summary>
    public long Id { get; set; }

    /// <summary>The name shown for the batch.</summary>
    public required string Name { get; set; }

    /// <summary>The folder every download of the batch is saved in.</summary>
    public required string Directory { get; set; }

    /// <summary>Extract the archive sets once every part has downloaded.</summary>
    public bool AutoExtract { get; set; } = true;

    /// <summary>Delete the archive parts after a successful extraction.</summary>
    public bool DeleteParts { get; set; }

    /// <summary>Where the batch is in its life.</summary>
    public BatchStatus Status { get; set; } = BatchStatus.Downloading;

    /// <summary>A short sentence for the user: why the batch needs attention, or how it finished.</summary>
    public string? Message { get; set; }

    /// <summary>Folders the sets were extracted into.</summary>
    public List<string> ExtractedTo { get; set; } = [];

    /// <summary>Automatic retries used per download id.</summary>
    public Dictionary<long, int> Retries { get; set; } = [];

    /// <summary>When the batch was made.</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When the batch reached Extracted or Done; null before that.</summary>
    public DateTime? FinishedUtc { get; set; }
}
