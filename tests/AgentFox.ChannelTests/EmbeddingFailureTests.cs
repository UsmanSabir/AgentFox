using AgentFox.Memory;

namespace AgentFox.ChannelTests;

/// <summary>
/// The local embedder fails in ways whose remedies have nothing in common, and the outermost
/// exception never names the cause. These pin the classification, because the cost of getting it
/// wrong is measured: a deployment was sent to 'doctor --fix' (which downloads the model) for an
/// intact model whose Visual C++ runtime was five years old.
/// </summary>
[TestClass]
public sealed class EmbeddingFailureTests
{
    /// <summary>
    /// The exact shape observed on the VM, 2026-09-18: ONNX Runtime 1.29 against a 14.28 runtime.
    /// 0x8007045A is Win32 1114, ERROR_DLL_INIT_FAILED.
    /// </summary>
    private static Exception NativeInitFailure() =>
        new TypeInitializationException(
            "Microsoft.ML.OnnxRuntime.NativeMethods",
            new DllNotFoundException(
                "Unable to load DLL 'onnxruntime' or one of its dependencies: " +
                "A dynamic link library (DLL) initialization routine failed. (0x8007045A)"));

    [TestMethod]
    public void Native_initialisation_failure_is_not_blamed_on_the_model()
    {
        var f = EmbeddingFailure.Diagnose(NativeInitFailure(), modelFilesPresent: true, isWindows: true);

        Assert.AreEqual(EmbeddingFailureKind.NativeRuntime, f.Kind);
        Assert.IsFalse(f.CanAutoFix, "Downloading the model cannot repair a native load failure.");
        StringAssert.Contains(f.Remedy, "vc_redist", StringComparison.OrdinalIgnoreCase);
        Assert.IsFalse(f.Remedy.Contains("doctor --fix", StringComparison.OrdinalIgnoreCase),
            "doctor --fix downloads the model and is the wrong advice here.");
    }

    [TestMethod]
    public void Native_failure_names_the_initialisation_case_specifically()
    {
        var f = EmbeddingFailure.Diagnose(NativeInitFailure(), modelFilesPresent: true, isWindows: true);

        StringAssert.Contains(f.Summary, "1114");
        StringAssert.Contains(f.Detail, "0x8007045A",
            "The OS error code is the whole diagnostic; it must survive into the detail line.");
    }

    [TestMethod]
    public void A_missing_dependency_is_reported_as_such_not_as_an_init_failure()
    {
        var ex = new TypeInitializationException(
            "Microsoft.ML.OnnxRuntime.NativeMethods",
            new DllNotFoundException(
                "Unable to load DLL 'onnxruntime': The specified module could not be found. (0x8007007E)"));

        var f = EmbeddingFailure.Diagnose(ex, modelFilesPresent: true, isWindows: true);

        Assert.AreEqual(EmbeddingFailureKind.NativeRuntime, f.Kind);
        StringAssert.Contains(f.Summary, "missing");
        Assert.IsFalse(f.Summary.Contains("1114"), "That code belongs to the other failure.");
    }

    [TestMethod]
    public void Non_windows_gets_a_remedy_that_exists_on_its_platform()
    {
        var f = EmbeddingFailure.Diagnose(NativeInitFailure(), modelFilesPresent: true, isWindows: false);

        Assert.AreEqual(EmbeddingFailureKind.NativeRuntime, f.Kind);
        Assert.IsFalse(f.Remedy.Contains("vc_redist", StringComparison.OrdinalIgnoreCase),
            "A Windows installer is not a remedy on Linux.");
        StringAssert.Contains(f.Remedy, "libgomp");
    }

    [TestMethod]
    public void Missing_model_files_stay_auto_fixable()
    {
        var ex = new FileNotFoundException(
            "Required embedding model file 'model.onnx' for model 'default' was not found.");

        var f = EmbeddingFailure.Diagnose(ex, modelFilesPresent: false, isWindows: true);

        Assert.AreEqual(EmbeddingFailureKind.ModelFiles, f.Kind);
        Assert.IsTrue(f.CanAutoFix);
        StringAssert.Contains(f.Remedy, "doctor --fix");
    }

    /// <summary>
    /// A native failure outranks absent model files. Repairing the model would leave the real cause
    /// in place and report success, which is worse than reporting nothing.
    /// </summary>
    [TestMethod]
    public void A_native_failure_wins_even_when_the_model_is_also_missing()
    {
        var f = EmbeddingFailure.Diagnose(NativeInitFailure(), modelFilesPresent: false, isWindows: true);

        Assert.AreEqual(EmbeddingFailureKind.NativeRuntime, f.Kind);
        Assert.IsFalse(f.CanAutoFix);
    }

    /// <summary>
    /// MEASURED 2026-09-18: Windows ships onnxruntime.dll 1.10 in System32. When the bundled 1.29
    /// fails to load the OS loader substitutes it, and the operator sees a version complaint that
    /// says nothing about the real cause. This is the text from the original bug report.
    /// </summary>
    [TestMethod]
    public void The_System32_copy_shadowing_the_bundled_one_is_recognised()
    {
        var ex = new TypeInitializationException(
            "Microsoft.ML.OnnxRuntime.NativeMethods",
            new InvalidOperationException(
                "The given version [14] is not supported, only version 1 to 10 is supported in this build."));

        var f = EmbeddingFailure.Diagnose(ex, modelFilesPresent: true, isWindows: true);

        Assert.AreEqual(EmbeddingFailureKind.NativeRuntime, f.Kind);
        StringAssert.Contains(f.Summary, "System32");
        Assert.IsFalse(f.CanAutoFix);
        StringAssert.Contains(f.Remedy, "vc_redist", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void An_unrecognised_failure_claims_no_remedy_it_does_not_have()
    {
        var f = EmbeddingFailure.Diagnose(
            new InvalidOperationException("something else entirely"),
            modelFilesPresent: true, isWindows: true);

        Assert.AreEqual(EmbeddingFailureKind.Unknown, f.Kind);
        Assert.IsFalse(f.CanAutoFix);
        StringAssert.Contains(f.Detail, "something else entirely");
    }

    [TestMethod]
    public void The_whole_chain_is_flattened_because_the_cause_is_never_outermost()
    {
        var detail = EmbeddingFailure.Flatten(NativeInitFailure());

        StringAssert.Contains(detail, "TypeInitializationException");
        StringAssert.Contains(detail, "DllNotFoundException");
        StringAssert.Contains(detail, "0x8007045A");
    }
}
