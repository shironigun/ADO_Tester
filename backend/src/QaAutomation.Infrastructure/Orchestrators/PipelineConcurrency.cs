namespace QaAutomation.Infrastructure.Orchestrators;

/// <summary>
/// Process-wide concurrency cap for LLM pipelines (spec B5 — Python <c>PIPELINE_SEM</c>). Both
/// orchestrators acquire <see cref="Gate"/> inside their pipeline runner, so a fan-out Epic/Feature
/// run can't stampede the LLM provider. Children beyond the cap show <see cref="QueuedActivity"/>
/// until a slot frees. Singleton.
/// </summary>
public sealed class PipelineConcurrency
{
    /// <summary>Max concurrent Analyst/Tester/Bug-Writer pipelines across the whole process.</summary>
    public SemaphoreSlim Gate { get; } = new(4, 4);

    public const string QueuedActivity = "Queued — waiting for a slot";
}
