namespace Microsoft.VisualStudio.Services.Agent.Worker.LegacyTestResults
{
    public interface IConverter<in TResultData, in TResultWebApi>
    {
        void Convert(TResultData resultData, TResultWebApi webApiData);
    }
}