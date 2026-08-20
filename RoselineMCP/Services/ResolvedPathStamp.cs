using RoselineMCP.Interfaces;

namespace RoselineMCP.Services;

/// <summary>
/// Carries the resolved checkout path from the only place that knows it — a service method holding
/// a live <see cref="LoadedProject"/> — to the only place that builds the failure envelope,
/// <c>ToolExecutionHelper.Error&lt;T&gt;</c>, which does not.
/// </summary>
/// <remarks>
/// <para>
/// The two are separated by a throw. A tool passes the caller's raw <c>string? project</c> to a
/// service and catches whatever comes back; the service loads internally, so <c>loaded</c> is a
/// local that no longer exists by the time the <c>catch</c> block runs. Passing the path as an
/// argument is therefore not reachable from any of the tools' <c>Error&lt;T&gt;</c> call sites —
/// the path has to travel <em>on the exception</em>, which is what <see cref="Exception.Data"/> is
/// for.
/// </para>
/// <para>
/// A wrapper exception type carrying the path was the alternative and was rejected: every
/// <c>Classify</c> arm and every tool <c>catch</c> would have to learn to unwrap it, for no extra
/// expressiveness. The stamp works uniformly across the BCL exception types this codebase throws
/// (<see cref="KeyNotFoundException"/>, <see cref="FileNotFoundException"/>,
/// <see cref="ArgumentException"/>, …) and — the property that makes it correct rather than merely
/// convenient — an exception raised <em>before</em> a project was resolved simply carries no stamp,
/// which yields an <b>absent</b> <c>resolvedPath</c> rather than an empty one.
/// </para>
/// </remarks>
public static class ResolvedPathStamp
{
    /// <summary>
    /// The <see cref="Exception.Data"/> key the resolved path travels under. Matches the
    /// <c>resolvedPath</c> JSON property it ends up in, on both the success and failure envelopes.
    /// </summary>
    public const string DataKey = "resolvedPath";

    /// <summary>
    /// Records on <paramref name="ex"/> the checkout that answered the call, so the failure
    /// envelope can name it. A no-op when there is nothing meaningful to record.
    /// </summary>
    /// <param name="ex">The in-flight exception, about to be rethrown.</param>
    /// <param name="loaded">The project handle that was live when <paramref name="ex"/> was thrown.</param>
    public static void Stamp(Exception ex, LoadedProject loaded) => Stamp(ex, loaded.ResolvedPath);

    /// <summary>
    /// Records <paramref name="resolvedPath"/> on <paramref name="ex"/>. Deliberately a no-op when
    /// the path is null or empty (an in-memory solution has no path, and reporting <c>""</c> would
    /// claim "resolved to nothing" where the truth is "nothing to report"), and when a stamp is
    /// already present — the innermost frame is the one closest to the throw, so it wins.
    /// </summary>
    /// <param name="ex">The in-flight exception, about to be rethrown.</param>
    /// <param name="resolvedPath">The absolute <c>.sln</c>/<c>.csproj</c> that answered the call.</param>
    public static void Stamp(Exception ex, string? resolvedPath)
    {
        if (string.IsNullOrEmpty(resolvedPath))
        {
            return;
        }

        // Exception.Data is virtual and documented as possibly null; a custom exception type could
        // return null, and losing the path is strictly better than turning a real failure into a
        // NullReferenceException raised from the stamping code itself.
        var data = ex.Data;
        if (data is null || data.IsReadOnly || data.Contains(DataKey))
        {
            return;
        }

        data[DataKey] = resolvedPath;
    }

    /// <summary>
    /// Reads back the path stamped by <see cref="Stamp(Exception, string?)"/>, or
    /// <see langword="null"/> when this exception was raised before any project was resolved.
    /// </summary>
    /// <param name="ex">
    /// The exception being turned into a failure envelope. <see langword="null"/> is accepted and
    /// yields <see langword="null"/>: some envelopes are built from no exception at all (a
    /// validation failure caught at the tool boundary), and "no exception" and "no stamp" mean the
    /// same thing here — nothing was resolved.
    /// </param>
    /// <returns>The absolute resolved path, or <see langword="null"/> when none was stamped.</returns>
    public static string? Read(Exception? ex)
    {
        var data = ex?.Data;
        return data is not null && data.Contains(DataKey) ? data[DataKey] as string : null;
    }
}
