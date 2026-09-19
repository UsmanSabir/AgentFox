using System.Text;

namespace AgentFox.Memory;

/// <summary>What stopped the local embedder from starting.</summary>
public enum EmbeddingFailureKind
{
    /// <summary>The ONNX Runtime native library could not be loaded or initialised.</summary>
    NativeRuntime,

    /// <summary>The model or vocabulary file could not be found.</summary>
    ModelFiles,

    /// <summary>Something this code cannot account for.</summary>
    Unknown
}

/// <summary>
/// Why <see cref="LocalEmbeddingService"/> could not be constructed, in the operator's words.
/// <para>
/// This exists because the exception alone is useless: the failure surfaces as
/// <c>TypeInitializationException</c> for <c>Microsoft.ML.OnnxRuntime.NativeMethods</c>, whose
/// <c>Message</c> names neither the cause nor the remedy. Printing only that message sent a real
/// deployment to "run doctor --fix" — which downloads the model — for a problem the model had no
/// part in (measured 2026-09-18: an up-to-date model, an intact DLL, and a five-year-old Visual
/// C++ runtime). The inner chain held the answer the whole time.
/// </para>
/// </summary>
/// <param name="Kind">The classification the remedy is chosen from.</param>
/// <param name="Summary">One line: what actually went wrong.</param>
/// <param name="Remedy">One line: what the operator should do about it.</param>
/// <param name="Detail">The flattened exception chain, which carries the OS error code.</param>
public sealed record EmbeddingFailure(
    EmbeddingFailureKind Kind,
    string Summary,
    string Remedy,
    string Detail)
{
    /// <summary>Only missing model files can be repaired without the operator installing something.</summary>
    public bool CanAutoFix => Kind == EmbeddingFailureKind.ModelFiles;

    private const string VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    /// <summary>
    /// Classifies a local-embedder construction failure.
    /// </summary>
    /// <param name="ex">The exception thrown while constructing the embedder.</param>
    /// <param name="modelFilesPresent">
    /// Result of <c>LocalEmbedder.TryEnsureModelFiles()</c>. Passed in rather than probed so this
    /// stays a pure function, and so the doctor can classify without touching the disk twice.
    /// </param>
    /// <param name="isWindows">Platform override for testing; defaults to the running OS.</param>
    public static EmbeddingFailure Diagnose(Exception ex, bool modelFilesPresent, bool? isWindows = null)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var windows = isWindows ?? OperatingSystem.IsWindows();
        var detail = Flatten(ex);

        if (IsNativeLoadFailure(ex, detail))
            return NativeRuntimeFailure(detail, windows);

        if (!modelFilesPresent || MentionsModelFile(detail))
        {
            return new EmbeddingFailure(
                EmbeddingFailureKind.ModelFiles,
                "The local embedding model files are missing.",
                "Run 'AgentFox doctor --fix' to download or restore them (about 22 MB).",
                detail);
        }

        return new EmbeddingFailure(
            EmbeddingFailureKind.Unknown,
            "The local embedder could not start.",
            "No automatic remedy is known for this failure. The detail below is what the runtime reported.",
            detail);
    }

    private static EmbeddingFailure NativeRuntimeFailure(string detail, bool windows)
    {
        // 0x8007045A == Win32 1114 ERROR_DLL_INIT_FAILED. The library was found and its
        // dependencies bound; its initialisation routine is what failed. On Windows that is
        // overwhelmingly a Visual C++ runtime older than the toolset ONNX Runtime was built with
        // — the imports still resolve by name, so nothing reports a missing dependency.
        var initialisationFailed =
            detail.Contains("0x8007045A", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("initialization routine failed", StringComparison.OrdinalIgnoreCase);

        // 0x8007007E == Win32 126 ERROR_MOD_NOT_FOUND: the library or a dependency is absent.
        var dependencyMissing =
            detail.Contains("0x8007007E", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("specified module could not be found", StringComparison.OrdinalIgnoreCase);

        string summary;
        if (initialisationFailed)
            summary = "The ONNX Runtime native library was found, but its initialisation failed (Windows error 1114).";
        else if (dependencyMissing)
            summary = "The ONNX Runtime native library could not be loaded: it or one of its dependencies is missing.";
        else if (IsShadowedBySystemCopy(detail))
            summary = "An older ONNX Runtime was loaded from C:\\Windows\\System32 instead of the one shipped with AgentFox. " +
                      "Windows ships its own onnxruntime.dll, and the OS loader falls back to it whenever the bundled " +
                      "copy fails to load — so the version complaint below is a symptom, not the cause.";
        else
            summary = "The ONNX Runtime native library could not be loaded.";

        var remedy = windows
            ? $"Install the Microsoft Visual C++ 2015-2022 x64 Redistributable and restart AgentFox: {VcRedistUrl}"
            : "Install the ONNX Runtime native prerequisites for this platform (on Debian/Ubuntu: 'apt install libgomp1').";

        return new EmbeddingFailure(EmbeddingFailureKind.NativeRuntime, summary, remedy, detail);
    }

    /// <summary>
    /// The managed binding asks for a C API version the loaded library is too old to provide.
    /// MEASURED 2026-09-18: Windows ships onnxruntime.dll 1.10 in System32, so a bundled 1.29 that
    /// fails to load is silently replaced by it and the visible error becomes a version mismatch.
    /// The remedy is still to make the bundled copy loadable; never to touch a Windows system file.
    /// </summary>
    private static bool IsShadowedBySystemCopy(string detail) =>
        detail.Contains("is not supported", StringComparison.OrdinalIgnoreCase) &&
        detail.Contains("only version", StringComparison.OrdinalIgnoreCase);

    private static bool IsNativeLoadFailure(Exception ex, string detail)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is DllNotFoundException or BadImageFormatException)
                return true;

            if (e is TypeInitializationException tie &&
                tie.TypeName.Contains("OnnxRuntime", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Fallback for a runtime that wraps the load failure in a plain exception, and for the
        // System32 shadowing above — which surfaces as a version complaint with no load error at all.
        return IsShadowedBySystemCopy(detail) ||
               (detail.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase) &&
                detail.Contains("unable to load", StringComparison.OrdinalIgnoreCase));
    }

    private static bool MentionsModelFile(string detail) =>
        detail.Contains("model.onnx", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("vocab.txt", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Renders the whole exception chain. The cause is never in the outermost message here, so a
    /// summary that stops at <c>ex.Message</c> discards the only diagnostic the runtime provided.
    /// </summary>
    public static string Flatten(Exception ex)
    {
        var sb = new StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (sb.Length > 0) sb.Append(" -> ");
            sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
        }
        return sb.ToString();
    }
}
