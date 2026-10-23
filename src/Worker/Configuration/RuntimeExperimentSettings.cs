namespace Worker.Configuration;

// The Experiment settings the Worker started with, and the ones in force now (changed by PUT /admin/experiment).
internal sealed class RuntimeExperimentSettings
{
    public ExperimentSettings Initial { get; }

    public ExperimentSettings Current { get; set; }

    public RuntimeExperimentSettings(ExperimentSettings initial)
    {
        Initial = initial;
        Current = initial;
    }
}