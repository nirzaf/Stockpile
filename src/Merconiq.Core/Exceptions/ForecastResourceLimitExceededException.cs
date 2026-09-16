namespace Merconiq.Core.Exceptions;

/// <summary>Indicates a forecast request exceeds a configured, non-truncating work limit.</summary>
public sealed class ForecastResourceLimitExceededException : ArgumentException
{
    public ForecastResourceLimitExceededException(string resource, int maximum, int observedAtLeast)
        : base($"Forecast request exceeds the configured {resource} limit of {maximum}; at least {observedAtLeast} records match. Reduce the request scope or raise the configured limit within its supported ceiling.")
    {
        Resource = resource;
        Maximum = maximum;
        ObservedAtLeast = observedAtLeast;
    }

    public string Resource { get; }

    public int Maximum { get; }

    public int ObservedAtLeast { get; }
}
