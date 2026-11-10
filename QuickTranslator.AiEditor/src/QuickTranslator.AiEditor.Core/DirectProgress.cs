namespace QuickTranslator.AiEditor.Core;

/// <summary>
/// Progress that runs at once on the reporting thread. Progress&lt;T&gt; created off the UI thread posts each report to
/// the thread pool, so reports can arrive out of order or after the operation has returned.
/// </summary>
internal sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
